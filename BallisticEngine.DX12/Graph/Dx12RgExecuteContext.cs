using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. What a pass's execute callback receives.
//
// Resolves virtual handles to the realised ID3D12Resource and hands out per-resource RTV/DSV/SRV/UAV
// descriptors on graph-owned descriptor heaps (lazily created, cached per-Entry per-compile). The
// pass records into List — barriers for its declared accesses were already emitted by the graph just
// before this callback fires (Granite invalidate/flush), so the pass body is barrier-free for the
// resources it declared.

public sealed class Dx12RgExecuteContext {
    public ID3D12GraphicsCommandList4 List { get; internal set; }
    public Dx12FrameContext Frame { get; internal set; }
    public int FrameIndex { get; internal set; }
    public Dx12RgQueue Queue { get; internal set; }

    readonly Dx12RgResourceRegistry registry;
    readonly Dx12RgDescriptorCache descriptors;

    internal Dx12RgExecuteContext(Dx12RgResourceRegistry registry, Dx12RgDescriptorCache descriptors) {
        this.registry = registry;
        this.descriptors = descriptors;
    }

    public ID3D12Resource Resolve(in Dx12RgHandle h) {
        var e = registry.Get(h);
        if (e.Resource is null)
            throw new InvalidOperationException(
                $"[Dx12Rg] resource '{e.Name}' has no live ID3D12Resource at execute — it was culled or never realised.");
        return e.Resource;
    }

    public ResourceStates StateOf(in Dx12RgHandle h) => registry.Get(h).CurrentState;

    public CpuDescriptorHandle Rtv(in Dx12RgHandle h) => descriptors.Rtv(registry.Get(h));
    public CpuDescriptorHandle Dsv(in Dx12RgHandle h) => descriptors.Dsv(registry.Get(h));
    public CpuDescriptorHandle Srv(in Dx12RgHandle h, Format? viewFormat = null) => descriptors.Srv(registry.Get(h), viewFormat);
    public CpuDescriptorHandle Uav(in Dx12RgHandle h, Format? viewFormat = null) => descriptors.Uav(registry.Get(h), viewFormat);
}

// Lazily creates and caches non-shader-visible CPU descriptors for transients/imports realised by
// the graph. RTV/DSV go on dedicated graph-owned heaps; SRV/UAV go on the engine's shared SrvStore
// (shader-visible staging path is the renderer's job — these are CPU handles for binding/copying).
// Reset() per compile drops cached descriptor indices because a transient's resource changes
// identity each frame (placed afresh on the aliasing heap).
public sealed class Dx12RgDescriptorCache : IDisposable {
    readonly Dx12Device dev;

    ID3D12DescriptorHeap rtvHeap, dsvHeap;
    int rtvCap, dsvCap, rtvCursor, dsvCursor;
    uint rtvInc, dsvInc;
    CpuDescriptorHandle rtvStart, dsvStart;

    readonly Dictionary<int, int> rtvByEntry = new();
    readonly Dictionary<int, int> dsvByEntry = new();
    readonly Dictionary<int, int> srvByEntry = new();
    readonly Dictionary<int, int> uavByEntry = new();
    readonly List<int> srvIndices = new();   // allocated on Dx12Backend.SrvStore — freed on Reset

    public Dx12RgDescriptorCache(Dx12Device dev, int rtvCapacity = 64, int dsvCapacity = 16) {
        this.dev = dev;
        rtvCap = rtvCapacity; dsvCap = dsvCapacity;
        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, (uint)rtvCap));
        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, (uint)dsvCap));
        rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        dsvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        dsvStart = dsvHeap.GetCPUDescriptorHandleForHeapStart();
    }

    public void Reset() {
        rtvCursor = 0; dsvCursor = 0;
        rtvByEntry.Clear(); dsvByEntry.Clear();
        srvByEntry.Clear(); uavByEntry.Clear();
        foreach (int i in srvIndices) Dx12Backend.SrvStore?.Free(i);
        srvIndices.Clear();
    }

    public CpuDescriptorHandle Rtv(Dx12RgResourceRegistry.Entry e) {
        if (rtvByEntry.TryGetValue(e.Id, out int idx)) return RtvHandle(idx);
        if (rtvCursor >= rtvCap) throw new InvalidOperationException("[Dx12Rg] RTV descriptor heap full — grow rtvCapacity.");
        idx = rtvCursor++;
        rtvByEntry[e.Id] = idx;
        dev.Device.CreateRenderTargetView(e.Resource, null, RtvHandle(idx));
        return RtvHandle(idx);
    }

    public CpuDescriptorHandle Dsv(Dx12RgResourceRegistry.Entry e) {
        if (dsvByEntry.TryGetValue(e.Id, out int idx)) return DsvHandle(idx);
        if (dsvCursor >= dsvCap) throw new InvalidOperationException("[Dx12Rg] DSV descriptor heap full — grow dsvCapacity.");
        idx = dsvCursor++;
        dsvByEntry[e.Id] = idx;
        var fmt = e.Imported ? e.Resource.Description.Format : e.Desc.Format;
        dev.Device.CreateDepthStencilView(e.Resource, new DepthStencilViewDescription {
            Format = fmt, ViewDimension = DepthStencilViewDimension.Texture2D,
        }, DsvHandle(idx));
        return DsvHandle(idx);
    }

    public CpuDescriptorHandle Srv(Dx12RgResourceRegistry.Entry e, Format? viewFormat) {
        var store = Dx12Backend.SrvStore ?? throw new InvalidOperationException("[Dx12Rg] SrvStore not initialised.");
        if (srvByEntry.TryGetValue(e.Id, out int idx)) return store.Cpu(idx);
        idx = store.Allocate();
        srvByEntry[e.Id] = idx; srvIndices.Add(idx);
        ShaderResourceViewDescription d = e.Kind == Dx12RgHandleKind.Buffer
            ? BufferSrv(e)
            : TextureSrv(e, viewFormat);
        dev.Device.CreateShaderResourceView(e.Resource, d, store.Cpu(idx));
        return store.Cpu(idx);
    }

    public CpuDescriptorHandle Uav(Dx12RgResourceRegistry.Entry e, Format? viewFormat) {
        var store = Dx12Backend.SrvStore ?? throw new InvalidOperationException("[Dx12Rg] SrvStore not initialised.");
        if (uavByEntry.TryGetValue(e.Id, out int idx)) return store.Cpu(idx);
        idx = store.Allocate();
        uavByEntry[e.Id] = idx; srvIndices.Add(idx);
        UnorderedAccessViewDescription d = e.Kind == Dx12RgHandleKind.Buffer
            ? BufferUav(e)
            : TextureUav(e, viewFormat);
        dev.Device.CreateUnorderedAccessView(e.Resource, null, d, store.Cpu(idx));
        return store.Cpu(idx);
    }

    static ShaderResourceViewDescription TextureSrv(Dx12RgResourceRegistry.Entry e, Format? viewFormat) {
        var fmt = viewFormat ?? (e.Imported ? e.Resource.Description.Format : e.Desc.Format);
        if (!e.Imported && e.Desc.Type == Dx12RgResourceType.Texture3D)
            return new ShaderResourceViewDescription {
                Format = fmt, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture3D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture3D = new Texture3DShaderResourceView { MipLevels = (uint)e.Desc.MipLevels, MostDetailedMip = 0 },
            };
        return new ShaderResourceViewDescription {
            Format = fmt, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView {
                MipLevels = (uint)(e.Imported ? 1 : e.Desc.MipLevels), MostDetailedMip = 0 },
        };
    }

    static UnorderedAccessViewDescription TextureUav(Dx12RgResourceRegistry.Entry e, Format? viewFormat) {
        var fmt = viewFormat ?? (e.Imported ? e.Resource.Description.Format : e.Desc.Format);
        if (!e.Imported && e.Desc.Type == Dx12RgResourceType.Texture3D)
            return new UnorderedAccessViewDescription {
                Format = fmt, ViewDimension = UnorderedAccessViewDimension.Texture3D,
                Texture3D = new Texture3DUnorderedAccessView { MipSlice = 0, FirstWSlice = 0, WSize = (uint)e.Desc.Depth },
            };
        return new UnorderedAccessViewDescription {
            Format = fmt, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
        };
    }

    static ShaderResourceViewDescription BufferSrv(Dx12RgResourceRegistry.Entry e) {
        long bytes = e.Imported ? (long)e.Resource.Description.Width : e.Desc.ByteSize;
        uint elems = (uint)Math.Max(1, bytes / 4);
        return new ShaderResourceViewDescription {
            Format = Format.R32_Typeless,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = elems, Flags = BufferShaderResourceViewFlags.Raw, StructureByteStride = 0 },
        };
    }

    static UnorderedAccessViewDescription BufferUav(Dx12RgResourceRegistry.Entry e) {
        long bytes = e.Imported ? (long)e.Resource.Description.Width : e.Desc.ByteSize;
        uint elems = (uint)Math.Max(1, bytes / 4);
        return new UnorderedAccessViewDescription {
            Format = Format.R32_Typeless,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView {
                FirstElement = 0, NumElements = elems, Flags = BufferUnorderedAccessViewFlags.Raw, StructureByteStride = 0 },
        };
    }

    CpuDescriptorHandle RtvHandle(int idx) => new(rtvStart, idx, rtvInc);
    CpuDescriptorHandle DsvHandle(int idx) => new(dsvStart, idx, dsvInc);

    public void Dispose() {
        foreach (int i in srvIndices) Dx12Backend.SrvStore?.Free(i);
        srvIndices.Clear();
        rtvHeap?.Dispose();
        dsvHeap?.Dispose();
    }
}
