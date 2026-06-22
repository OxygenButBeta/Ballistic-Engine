namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. A pass node.
//
// A pass is pure data after setup: its declared reads/writes (handle + intended usage state) and a
// deferred execute callback. No GPU work happens at setup; the graph compiles the whole frame from
// these declarations (DAG, cull, alias, barriers) before any callback runs. Mirrors UE-RDG's
// AddPass(Parameters, [](FRHICommandList&){...}) and the old Dx12PassBuilder shape so porting the
// existing IRenderPass passes onto this graph is mechanical.

public enum Dx12RgQueue { Graphics, AsyncCompute }

public sealed class Dx12RgPass {
    public readonly struct Access {
        public readonly Dx12RgHandle Handle;
        public readonly Dx12RgResourceState State;
        public Access(Dx12RgHandle h, Dx12RgResourceState s) { Handle = h; State = s; }
    }

    public string Name { get; }
    public Dx12RgQueue Queue { get; }
    public int Index { get; internal set; }            // registration index

    // A NeverCull pass is always executed even if its writes have no live consumer (e.g. a pass with
    // pure side effects, a debug readback, or a present). It also seeds nothing into the cull stack
    // beyond its own keep-alive.
    public bool NeverCull { get; internal set; }

    public readonly List<Access> Reads = new();
    public readonly List<Access> Writes = new();

    // Transients this pass declared via builder.CreateTransient — used by the report only; the
    // registry owns the actual entries.
    public readonly List<Dx12RgHandle> Created = new();

    public Action<Dx12RgExecuteContext> Execute { get; }

    // --- compile-phase scratch (filled by Dx12RgGraph) ------------------------------------------
    internal int RefCount;                 // Frostbite refcount-cull: number of live consumers of its writes
    internal bool Culled;
    internal int Order = -1;               // position in the linearized executed order
    internal readonly List<int> Producers = new();   // passes this one reads-from (DAG edges, for cull)

    public Dx12RgPass(string name, Dx12RgQueue queue, Action<Dx12RgExecuteContext> execute) {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Queue = queue;
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    internal void DeclareRead(in Dx12RgHandle h, Dx12RgResourceState s) => Reads.Add(new Access(h, s));
    internal void DeclareWrite(in Dx12RgHandle h, Dx12RgResourceState s) => Writes.Add(new Access(h, s));
}
