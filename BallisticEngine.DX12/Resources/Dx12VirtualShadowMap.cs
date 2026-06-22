using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12VirtualShadowMap : IDisposable {
    public const Format DepthFormat = Format.D32_Float;
    public const int MaxLevels = 16;

    public int Resolution { get; }
    public int Levels { get; }
    public float Level0Extent { get; }
    public ID3D12Resource Resource { get; }

    readonly Dx12Device dev;
    readonly ID3D12DescriptorHeap dsvHeap;
    readonly uint dsvInc;
    int srvIndex = -1;
    ResourceStates state;

    public readonly Matrix4x4[] LightMatrices = new Matrix4x4[MaxLevels];
    public readonly Vector3[] SnappedCenter = new Vector3[MaxLevels];
    public readonly float[] LevelExtent = new float[MaxLevels];

    readonly Vector3[] lastSnappedCenter = new Vector3[MaxLevels];
    readonly bool[] levelEverRendered = new bool[MaxLevels];
    int lastCasterStamp = int.MinValue;
    public readonly bool[] LevelDirty = new bool[MaxLevels];

    public Dx12VirtualShadowMap(Dx12Device device, int resolution, int levels, float level0Extent) {
        dev = device;
        Resolution = resolution;
        Levels = Math.Clamp(levels, 1, MaxLevels);
        Level0Extent = level0Extent;

        var desc = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)resolution, (uint)resolution,
            arraySize: (ushort)Levels, mipLevels: 1);
        desc.Flags = ResourceFlags.AllowDepthStencil;
        var clear = new ClearValue(DepthFormat, 1.0f, 0);
        Resource = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.DepthWrite, clear);
        Resource.Name = "VirtualShadowClipmap";
        state = ResourceStates.DepthWrite;

        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, (uint)Levels));
        dsvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        for (int c = 0; c < Levels; c++) {
            var dsvDesc = new DepthStencilViewDescription {
                Format = DepthFormat,
                ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView {
                    MipSlice = 0, FirstArraySlice = (uint)c, ArraySize = 1,
                },
            };
            dev.Device.CreateDepthStencilView(Resource, dsvDesc, DsvHandle(c));
        }

        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DArray,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2DArray = new Texture2DArrayShaderResourceView {
                MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = (uint)Levels,
            },
        };
        dev.Device.CreateShaderResourceView(Resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    CpuDescriptorHandle DsvHandle(int level) =>
        new(dsvHeap.GetCPUDescriptorHandleForHeapStart(), level, dsvInc);

    public void Fit(Vector3 cameraPos, Vector3 lightTravelDir, int casterStamp, bool cacheOn) {
        Vector3 lightDir = Vector3.Normalize(lightTravelDir);
        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        bool casterChanged = casterStamp != lastCasterStamp;

        for (int i = 0; i < Levels; i++) {
            float extent = Level0Extent * MathF.Pow(2f, i);
            LevelExtent[i] = extent;
            float casterBackup = extent * 2f + 60f;

            Matrix4x4 lightView0 = Matrix4x4.CreateLookAt(cameraPos - lightDir * casterBackup, cameraPos, up);
            float texelSize = extent * 2f / Resolution;
            Vector3 centerLs = Vector3.Transform(cameraPos, lightView0);
            centerLs.X = MathF.Floor(centerLs.X / texelSize) * texelSize;
            centerLs.Y = MathF.Floor(centerLs.Y / texelSize) * texelSize;
            Matrix4x4.Invert(lightView0, out Matrix4x4 invLightView0);
            Vector3 center = Vector3.Transform(centerLs, invLightView0);
            SnappedCenter[i] = center;

            Matrix4x4 lightView = Matrix4x4.CreateLookAt(center - lightDir * casterBackup, center, up);
            Matrix4x4 lightProj = Matrix4x4.CreateOrthographic(extent * 2f, extent * 2f, 0.1f,
                casterBackup + extent * 2f);
            LightMatrices[i] = lightView * lightProj;

            bool moved = !levelEverRendered[i] ||
                         (center - lastSnappedCenter[i]).LengthSquared() > 1e-8f;
            LevelDirty[i] = !cacheOn || casterChanged || moved;
        }
        lastCasterStamp = casterStamp;
    }

    public void MarkRendered(int level) {
        levelEverRendered[level] = true;
        lastSnappedCenter[level] = SnappedCenter[level];
    }

    public void RenderLevel(ID3D12GraphicsCommandList4 cl, int level, bool clear,
        Action<ID3D12GraphicsCommandList4> record) {
        TransitionTo(cl, ResourceStates.DepthWrite);
        cl.RSSetViewport(0, 0, Resolution, Resolution);
        cl.RSSetScissorRect(Resolution, Resolution);
        CpuDescriptorHandle dsv = DsvHandle(level);
        cl.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, dsv);
        if (clear) cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
        record(cl);
    }

    public void ToShaderResource(ID3D12GraphicsCommandList4 cl) =>
        TransitionTo(cl, ResourceStates.PixelShaderResource);

    public void ToDepthWrite(ID3D12GraphicsCommandList4 cl) =>
        TransitionTo(cl, ResourceStates.DepthWrite);

    void TransitionTo(ID3D12GraphicsCommandList4 cl, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(Resource, state, target);
        state = target;
    }

    public void Dispose() {
        dsvHeap.Dispose();
        Resource.Dispose();
    }
}