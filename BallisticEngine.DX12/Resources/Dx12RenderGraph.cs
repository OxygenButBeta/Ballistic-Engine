using System.Text;

namespace BallisticEngine.DX12;

public sealed class Dx12RenderGraph {
    readonly List<IRenderPass> registered = new();
    IRenderPass[] ordered = Array.Empty<IRenderPass>();
    bool built;

    int coreCount = -1;

    IRenderPass[] graphOrder;
    Dx12GraphResources graphResources;
    Dx12PassDeclaration[] declarations;
    string lastCompileReport;

    Dx12BarrierDeriver deriver;
    bool barriersDerived;
    string lastDeriverReport;

    readonly Action<string, Action> timePass;

    public Dx12RenderGraph(Action<string, Action> timePass = null) {
        this.timePass = timePass;
    }

    public void Add(IRenderPass pass) {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        registered.Add(pass);
        built = false;
        graphOrder = null;
    }

    public void Build() {
        ordered = registered.OrderBy(p => (int)p.Event).ToArray();
        built = true;
    }

    public void MarkCoreBoundary() => coreCount = registered.Count;

    public void SetFeaturePasses(IReadOnlyList<IRenderPass> features) {
        if (coreCount < 0) coreCount = registered.Count;
        if (registered.Count > coreCount) registered.RemoveRange(coreCount, registered.Count - coreCount);
        if (features != null)
            for (int i = 0; i < features.Count; i++) registered.Add(features[i]);
        built = false;
        graphOrder = null;
        Build();
        Compile();
    }

    public IReadOnlyList<IRenderPass> Passes => ordered;
    public int Count => registered.Count;
    public string LastCompileReport => lastCompileReport;

    public void Execute(Dx12FrameContext ctx) =>
        Execute(ctx, int.MinValue, int.MaxValue);

    public void Execute(Dx12FrameContext ctx, int minEventInclusive, int maxEventExclusive) {
        if (!built) Build();
        var list = ordered;
        var prof = ctx.Dev?.GpuProfiler;
        bool profOn = prof is { Enabled: true };
        for (int i = 0; i < list.Length; i++) {
            IRenderPass pass = list[i];
            int ev = (int)pass.Event;
            if (ev < minEventInclusive || ev >= maxEventExclusive) continue;
            if (!pass.Enabled(ctx)) continue;
            if (profOn && ctx.Dev.FrameList is { } fl) {
                prof.Begin(fl, pass.Name);
                if (timePass != null) timePass(pass.Name, () => pass.Record(ctx)); else pass.Record(ctx);
                if (ctx.Dev.FrameList is { } fl2) prof.End(fl2);
            }
            else if (timePass != null) timePass(pass.Name, () => pass.Record(ctx));
            else pass.Record(ctx);
        }
    }

    public void Compile() {
        if (!built) Build();
        int n = registered.Count;

        graphResources = new Dx12GraphResources();
        var builder = new Dx12PassBuilder(graphResources);
        declarations = new Dx12PassDeclaration[n];
        for (int i = 0; i < n; i++) {
            var decl = new Dx12PassDeclaration();
            builder.Begin(decl);
            registered[i].Declare(builder);
            decl.Declared = decl.Reads.Count + decl.Writes.Count + decl.SharedState.Count > 0;
            declarations[i] = decl;
        }

        var liveResourceIds = new HashSet<int>();
        for (int i = 0; i < n; i++) {
            foreach (int id in declarations[i].Reads) liveResourceIds.Add(id);
            foreach (int id in declarations[i].Writes) liveResourceIds.Add(id);
        }
        for (int i = 0; i < n; i++)
            if (declarations[i].IsOpaque)
                foreach (int id in liveResourceIds) declarations[i].Reads.Add(id);

        var adj = new List<int>[n];
        var indeg = new int[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        var lastWriter = new Dictionary<int, int>();
        var lastReaders = new Dictionary<int, List<int>>();
        var lastToucher = new Dictionary<string, int>();

        void AddEdge(int from, int to) {
            if (from == to) return;
            if (adj[from].Contains(to)) return;
            adj[from].Add(to); indeg[to]++;
        }

        int RegIndexOf(IRenderPass p) { for (int k = 0; k < n; k++) if (ReferenceEquals(registered[k], p)) return k; return -1; }
        foreach (IRenderPass pass in ordered) {
            int i = RegIndexOf(pass);
            var d = declarations[i];
            foreach (int id in d.Reads) {
                if (lastWriter.TryGetValue(id, out int w)) AddEdge(w, i);
                if (!lastReaders.TryGetValue(id, out var rl)) { rl = new List<int>(); lastReaders[id] = rl; }
                rl.Add(i);
            }
            foreach (int id in d.Writes) {
                if (lastWriter.TryGetValue(id, out int w)) AddEdge(w, i);
                if (lastReaders.TryGetValue(id, out var rl)) foreach (int r in rl) AddEdge(r, i);
                lastWriter[id] = i;
                lastReaders[id] = new List<int>();
            }
            foreach (string key in d.SharedState) {
                if (lastToucher.TryGetValue(key, out int t)) AddEdge(t, i);
                lastToucher[key] = i;
            }
        }

        var culled = new bool[n];
        bool changed = true;
        while (changed) {
            changed = false;
            for (int i = 0; i < n; i++) {
                if (culled[i]) continue;
                var d = declarations[i];
                if (!d.AllowCulling || d.IsOpaque || d.SharedState.Count > 0) continue;
                if (d.Writes.Count == 0) continue;
                bool anyKept = false;
                foreach (int id in d.Writes) {
                    if (graphResources.ById(id).Imported) { anyKept = true; break; }

                    foreach (int consumer in adj[i])
                        if (!culled[consumer] && declarations[consumer].Reads.Contains(id)) { anyKept = true; break; }
                    if (anyKept) break;
                }
                if (!anyKept) { culled[i] = true; changed = true; }
            }
        }

        var indegLive = (int[])indeg.Clone();
        for (int i = 0; i < n; i++)
            if (culled[i]) foreach (int to in adj[i]) indegLive[to]--;
        var ready = new PriorityQueue<int, long>();
        long Key(int i) => ((long)(int)registered[i].Event << 32) | (uint)i;
        for (int i = 0; i < n; i++)
            if (!culled[i] && indegLive[i] == 0) ready.Enqueue(i, Key(i));
        var order = new List<IRenderPass>(n);
        var orderIdx = new List<int>(n);
        int produced = 0;
        while (ready.Count > 0) {
            int i = ready.Dequeue();
            order.Add(registered[i]); orderIdx.Add(i); produced++;
            foreach (int to in adj[i]) {
                if (culled[to]) continue;
                if (--indegLive[to] == 0) ready.Enqueue(to, Key(to));
            }
        }
        int liveCount = 0; for (int i = 0; i < n; i++) if (!culled[i]) liveCount++;
        if (produced != liveCount)
            throw new InvalidOperationException(
                $"[Dx12RenderGraph] DAG has a CYCLE — topo-sort produced {produced} of {liveCount} live passes. " +
                "A pass's declared reads/writes formed a dependency loop (check Declare()).");

        graphOrder = order.ToArray();
        lastCompileReport = BuildReport(orderIdx, culled);

        deriver = new Dx12BarrierDeriver();
        for (int i = 0; i < n; i++)
            if (declarations[i].BarriersDerived)
                deriver.Register(registered[i].Name, declarations[i].Usages);
        lastDeriverReport = deriver.CompareToManual(Dx12BarrierDeriver.ManualReference(), out bool unsound);
        if (unsound)
            throw new InvalidOperationException(
                "[Dx12RenderGraph] V3 barrier derivation UNSOUND — derived set does not cover the manual reference " +
                "(same final state per role required). See report:\n" + lastDeriverReport);
    }

    public void SetBarriersDerived(bool on) => barriersDerived = on;
    public string LastDeriverReport => lastDeriverReport;

    public void ExecuteGraph(Dx12FrameContext ctx) {
        if (graphOrder == null) Compile();
        var list = graphOrder;
        for (int i = 0; i < list.Length; i++) {
            IRenderPass pass = list[i];
            if (!pass.Enabled(ctx)) continue;
            if (barriersDerived) deriver.Emit(pass.Name, ctx);
            if (timePass != null) timePass(pass.Name, () => pass.Record(ctx));
            else pass.Record(ctx);
        }
    }

    public IReadOnlyList<IRenderPass> GraphOrder => graphOrder;

    public int OrderIndexOf(string name) {
        var list = graphOrder;
        if (list == null) return -1;
        for (int i = 0; i < list.Length; i++) if (list[i].Name == name) return i;
        return -1;
    }

    string BuildReport(List<int> orderIdx, bool[] culled) {
        var sb = new StringBuilder();
        sb.AppendLine($"[Dx12RenderGraph] V1 compile: {registered.Count} passes, {graphResources.Count} resources.");
        sb.AppendLine("  Resources: " + string.Join(", ", graphResources.All.Select(r => r.ToString())));
        sb.AppendLine("  Topo order (event/regIdx):");
        foreach (int i in orderIdx) {
            var d = declarations[i];
            string kind = d.IsOpaque ? "opaque" : (d.AllowCulling ? "cullable" : "declared");
            sb.AppendLine($"    {registered[i].Name} [{registered[i].Event}={(int)registered[i].Event}] {kind} " +
                          $"R={d.Reads.Count} W={d.Writes.Count}" + (d.SharedState.Count > 0 ? $" shared={d.SharedState.Count}" : ""));
        }
        for (int i = 0; i < registered.Count; i++)
            if (culled[i]) sb.AppendLine($"  CULLED: {registered[i].Name} (no live consumer for its non-imported writes)");
        return sb.ToString();
    }

    public void Resize(int width, int height) {
        for (int i = 0; i < registered.Count; i++)
            registered[i].Resize(width, height);
    }
}
