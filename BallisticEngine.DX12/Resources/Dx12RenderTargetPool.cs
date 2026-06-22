using System.Text;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12RenderTargetPool : IDisposable {
    public static Dx12RenderTargetPool Active;

    readonly Dx12Device dev;

    sealed class PooledTarget {
        public string Name;
        public int Width, Height;
        public Format Format;
        public bool AllowUav;
        public long AllocBytes;
        public long AllocAlign;
        public int FirstWrite;
        public int LastRead;
        public int RegionId = -1;
        public Dx12OffscreenTarget Live;
    }

    sealed class AliasRegion {
        public ulong Offset;
        public long Bytes;
        public readonly List<int> Members = new();
        public int LastTenant = -1;
    }

    readonly List<PooledTarget> targets = new();
    readonly Dictionary<string, int> byName = new();
    readonly List<AliasRegion> regions = new();
    ID3D12Heap heap;
    ulong heapBytes;
    bool planBuilt;
    string planReport = "(alias plan not built)";

    public Dx12RenderTargetPool(Dx12Device device) { dev = device; }

    public string PlanReport => planReport;

    public static Dx12OffscreenTarget AllocOrPool(Dx12Device dev, string name, int width, int height,
        Format format, bool colorReadable, bool allowUav) {
        var pool = Active;
        if (pool != null && pool.planBuilt && pool.byName.ContainsKey(name))
            return pool.Acquire(name, width, height, format, colorReadable, allowUav);
        return new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: format,
            colorReadable: colorReadable, allowUav: allowUav);
    }

    public void Register(string name, int width, int height, Format format, bool allowUav,
                         int firstWritePass, int lastReadPass) {
        if (!byName.TryGetValue(name, out int idx)) {
            idx = targets.Count;
            byName[name] = idx;
            targets.Add(new PooledTarget { Name = name });
        }
        var t = targets[idx];
        t.Width = width; t.Height = height; t.Format = format; t.AllowUav = allowUav;
        t.FirstWrite = firstWritePass; t.LastRead = lastReadPass;
    }

    public void BuildPlan() {
        foreach (var t in targets) {
            var desc = ResourceDescription.Texture2D(t.Format, (uint)t.Width, (uint)t.Height, mipLevels: 1, arraySize: 1);
            desc.Flags = ResourceFlags.AllowRenderTarget | (t.AllowUav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);
            var info = dev.Device.GetResourceAllocationInfo(0, new[] { desc });
            t.AllocBytes = (long)info.SizeInBytes;
            t.AllocAlign = (long)info.Alignment;
        }

        regions.Clear();
        var order = Enumerable.Range(0, targets.Count).OrderBy(i => targets[i].FirstWrite).ThenBy(i => i).ToList();
        foreach (int i in order) {
            var t = targets[i];
            int chosen = -1;
            for (int r = 0; r < regions.Count; r++) {
                bool disjoint = regions[r].Members.All(m => targets[m].LastRead < t.FirstWrite);
                if (disjoint) { chosen = r; break; }
            }
            if (chosen < 0) { regions.Add(new AliasRegion()); chosen = regions.Count - 1; }
            regions[chosen].Members.Add(i);
            regions[chosen].Bytes = Math.Max(regions[chosen].Bytes, t.AllocBytes);
            t.RegionId = chosen;
        }

        const ulong Align = 64 * 1024;
        ulong cursor = 0;
        foreach (var reg in regions) {
            cursor = (cursor + Align - 1) & ~(Align - 1);
            reg.Offset = cursor;
            cursor += (ulong)reg.Bytes;
        }
        heapBytes = (cursor + Align - 1) & ~(Align - 1);
        if (heapBytes == 0) heapBytes = Align;

        heap?.Dispose();
        var heapDesc = new HeapDescription(heapBytes, HeapType.Default, Align,
            HeapFlags.AllowOnlyRenderTargetDepthStencilTextures);
        heap = dev.Device.CreateHeap<ID3D12Heap>(heapDesc);
        foreach (var reg in regions) reg.LastTenant = -1;

        planBuilt = true;
        planReport = BuildPlanReport();
    }

    public Dx12OffscreenTarget Acquire(string name, int width, int height, Format format, bool colorReadable, bool allowUav) {
        if (!planBuilt) throw new InvalidOperationException("[Dx12RenderTargetPool] Acquire before BuildPlan().");
        if (!byName.TryGetValue(name, out int idx))
            throw new InvalidOperationException($"[Dx12RenderTargetPool] '{name}' was not Register()ed in the plan.");
        var t = targets[idx];
        if (t.Width != width || t.Height != height || t.Format != format || t.AllowUav != allowUav)
            throw new InvalidOperationException(
                $"[Dx12RenderTargetPool] '{name}' footprint mismatch: registered {t.Width}x{t.Height} {t.Format} uav={t.AllowUav}, " +
                $"acquired {width}x{height} {format} uav={allowUav}. Re-Register with the actual footprint.");
        t.Live?.Dispose();
        var reg = regions[t.RegionId];
        t.Live = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: format,
            colorReadable: colorReadable, allowUav: allowUav, placedHeap: heap, placedOffset: reg.Offset);
        return t.Live;
    }

    public static void PoolBarrier(Dx12Device dev, params string[] producedNames) {
        var pool = Active;
        if (pool == null || !pool.planBuilt || pool.regions.Count == 0) return;
        pool.EmitBarrierAndDiscard(dev, producedNames);
    }

    void EmitBarrierAndDiscard(Dx12Device dev, string[] producedNames) {
        dev.ExecuteSync(cl => {
            foreach (string name in producedNames) {
                if (!byName.TryGetValue(name, out int idx)) continue;
                var t = targets[idx];
                var live = t.Live;
                if (live == null || t.RegionId < 0) continue;
                var reg = regions[t.RegionId];
                Dx12OffscreenTarget beforeTarget = (reg.LastTenant >= 0 && reg.LastTenant != idx)
                                                   ? targets[reg.LastTenant].Live : null;
                ID3D12Resource before = beforeTarget?.RenderTarget;
                cl.ResourceBarrierAliasing(before, live.RenderTarget);
                live.DiscardForAlias(cl);
                reg.LastTenant = idx;
            }
        });
    }

    string BuildPlanReport() {
        var sb = new StringBuilder();
        sb.AppendLine($"[Dx12RenderTargetPool] V2 alias plan: {targets.Count} pooled targets → {regions.Count} regions, " +
                      $"heap {heapBytes / 1024}KB (vs {targets.Sum(t => t.AllocBytes) / 1024}KB un-aliased; " +
                      $"saved {(targets.Sum(t => t.AllocBytes) - (long)heapBytes) / 1024}KB).");
        for (int r = 0; r < regions.Count; r++) {
            var reg = regions[r];
            sb.AppendLine($"  region {r} @offset {reg.Offset / 1024}KB size {reg.Bytes / 1024}KB: " +
                          string.Join(", ", reg.Members.Select(m => $"{targets[m].Name}[{targets[m].FirstWrite}..{targets[m].LastRead}]")));
        }
        return sb.ToString();
    }

    public string AuditNoOverlap() {
        foreach (var reg in regions) {
            for (int a = 0; a < reg.Members.Count; a++)
                for (int b = a + 1; b < reg.Members.Count; b++) {
                    var ta = targets[reg.Members[a]]; var tb = targets[reg.Members[b]];
                    bool disjoint = ta.LastRead < tb.FirstWrite || tb.LastRead < ta.FirstWrite;
                    if (!disjoint)
                        return $"OVERLAP: {ta.Name}[{ta.FirstWrite}..{ta.LastRead}] aliases {tb.Name}[{tb.FirstWrite}..{tb.LastRead}] in region (shared memory while both live)";
                }
        }
        return null;
    }

    public void Dispose() {
        foreach (var t in targets) t.Live?.Dispose();
        heap?.Dispose();
        if (ReferenceEquals(Active, this)) Active = null;
    }
}
