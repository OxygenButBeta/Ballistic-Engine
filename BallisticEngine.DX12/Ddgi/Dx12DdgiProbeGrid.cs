using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12DdgiProbeGrid : IDisposable
{
    public const int OctRes = 8;
    public const int OctTexels = OctRes * OctRes;
    public const int VisRes = 16;
    public const int VisTexels = VisRes * VisRes;

    readonly Dx12Device dev;

    public Dx12DdgiProbeGrid(Dx12Device device) { dev = device; }

    public int CountX { get; private set; }
    public int CountY { get; private set; }
    public int CountZ { get; private set; }
    public int ProbeCount => CountX * CountY * CountZ;

    public Vector3 GridOrigin { get; private set; }
    public Vector3 ProbeSpacing { get; private set; }

    ID3D12Resource irradA, irradB;
    bool writeB;
    public ID3D12Resource IrradianceWrite => writeB ? irradB : irradA;
    public ID3D12Resource IrradianceRead  => writeB ? irradA : irradB;
    public ulong IrradianceWriteGpu => IrradianceWrite?.GPUVirtualAddress ?? 0;
    public ulong IrradianceReadGpu  => IrradianceRead?.GPUVirtualAddress ?? 0;

    ID3D12Resource visA, visB;
    public ID3D12Resource VisibilityWrite => writeB ? visB : visA;
    public ID3D12Resource VisibilityRead  => writeB ? visA : visB;
    public ulong VisibilityWriteGpu => VisibilityWrite?.GPUVirtualAddress ?? 0;
    public ulong VisibilityReadGpu  => VisibilityRead?.GPUVirtualAddress ?? 0;

    ID3D12Resource probeState;
    public ID3D12Resource ProbeState => probeState;
    public ulong ProbeStateGpu => probeState?.GPUVirtualAddress ?? 0;
    public bool StatePlaced { get; private set; }

    public bool HistoryValid { get; private set; }
    public bool Valid { get; private set; }

    int dimStamp = -1;
    Vector3 lastMin, lastMax;

    public bool Ensure(Dx12FrameContext ctx, int reqX, int reqY, int reqZ,
                       bool useVolumeBounds = false, Vector3 volMin = default, Vector3 volMax = default)
    {
        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        if (sceneAS == null || !sceneAS.Valid || sceneAS.InstanceCount == 0) { Valid = false; return false; }

        Vector3 min, max;
        if (useVolumeBounds)
        {
            min = Vector3.Min(volMin, volMax);
            max = Vector3.Max(volMin, volMax);
        }
        else
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            for (int i = 0; i < sceneAS.InstanceCount; i++)
            {
                Mesh mesh = sceneAS.InstanceMesh(i);
                if (mesh == null) continue;
                mesh.GetLocalBounds(out Vector3 lo, out Vector3 hi);
                Matrix4x4 world = sceneAS.InstanceWorld(i);
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? lo.X : hi.X,
                        (c & 2) == 0 ? lo.Y : hi.Y,
                        (c & 4) == 0 ? lo.Z : hi.Z);
                    Vector3 w = Vector3.Transform(corner, world);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }
            }
        }
        if (min.X > max.X) { Valid = false; return false; }

        int dims = (reqX & 0x3ff) | ((reqY & 0x3ff) << 10) | ((reqZ & 0x3ff) << 20);
        float moveEps = MathF.Max(0.05f, 0.02f * Vector3.Distance(min, max));
        bool aabbMoved = Vector3.Distance(min, lastMin) > moveEps || Vector3.Distance(max, lastMax) > moveEps;
        bool layoutChanged = dims != dimStamp || aabbMoved || irradA == null;

        if (layoutChanged)
        {
            Vector3 size = Vector3.Max(max - min, new Vector3(0.01f));
            var counts = new Vector3(MathF.Max(reqX, 2), MathF.Max(reqY, 2), MathF.Max(reqZ, 2));
            Vector3 spacing = size / (counts - Vector3.One);

            CountX = reqX; CountY = reqY; CountZ = reqZ;
            ProbeSpacing = spacing;
            GridOrigin = min;

            int needProbes = ProbeCount;
            if (irradA == null || CurrentCapacityProbes < needProbes)
            {
                Realloc(needProbes);
                HistoryValid = false;
            }
            dimStamp = dims; lastMin = min; lastMax = max;
            StatePlaced = false;
        }

        Valid = irradA != null;
        return Valid;
    }

    int CurrentCapacityProbes;

    ID3D12Resource MakeBuffer(long bytes) =>
        dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);

    void Realloc(int probeCount)
    {
        irradA?.Dispose(); irradB?.Dispose(); visA?.Dispose(); visB?.Dispose(); probeState?.Dispose();
        long irrBytes = (long)probeCount * OctTexels * 16;
        long visBytes = (long)probeCount * VisTexels * 8;
        irradA = MakeBuffer(irrBytes); irradB = MakeBuffer(irrBytes);
        visA = MakeBuffer(visBytes);   visB = MakeBuffer(visBytes);
        probeState = MakeBuffer((long)probeCount * 16);
        CurrentCapacityProbes = probeCount;
        writeB = false;
        states.Clear();
        states[irradA] = states[irradB] = states[visA] = states[visB] = ResourceStates.UnorderedAccess;
        states[probeState] = ResourceStates.UnorderedAccess;
    }

    public Vector3 ProbePos(int ix, int iy, int iz) => GridOrigin + new Vector3(ix, iy, iz) * ProbeSpacing;

    public unsafe void PlaceProbes(Dx12Device device, GpuSceneQuery query)
    {
        if (probeState == null || StatePlaced) return;
        int n = ProbeCount;

        var pts = new Vector3[n];
        for (int iz = 0, p = 0; iz < CountZ; iz++)
            for (int iy = 0; iy < CountY; iy++)
                for (int ix = 0; ix < CountX; ix++, p++)
                    pts[p] = ProbePos(ix, iy, iz);

        float radius = 2.0f * MathF.Max(ProbeSpacing.X, MathF.Max(ProbeSpacing.Y, ProbeSpacing.Z));

        GpuSceneQuery.SpaceClass[] cls = query.ClassifySpace(pts, radius);
        Vector3[] nudged = query.NudgeToFreeSpace(pts, radius);

        float maxMove = 1.5f * MathF.Max(ProbeSpacing.X, MathF.Max(ProbeSpacing.Y, ProbeSpacing.Z));

        var state = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 offset = nudged[i] - pts[i];
            float move = offset.Length();
            bool solid = cls[i] == GpuSceneQuery.SpaceClass.Solid;

            if (move > maxMove)
            {
                offset = Vector3.Zero;
                state[i] = new Vector4(0, 0, 0, solid ? 0f : 1f);
            }
            else
            {
                bool active = !(solid && move < 1e-4f);
                state[i] = new Vector4(offset.X, offset.Y, offset.Z, active ? 1f : 0f);
            }
        }

        UploadState(device, state);
        StatePlaced = true;
    }

    unsafe void UploadState(Dx12Device device, Vector4[] state)
    {
        long bytes = (long)state.Length * 16;
        ID3D12Resource upload = device.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer((ulong)bytes), ResourceStates.GenericRead);
        byte* dst = upload.Map<byte>(0);
        fixed (Vector4* src = state) System.Buffer.MemoryCopy(src, dst, bytes, bytes);
        upload.Unmap(0);

        ResourceStates was = StateOf(probeState);
        device.ExecuteSync(cl =>
        {
            if (was != ResourceStates.CopyDest) cl.ResourceBarrierTransition(probeState, was, ResourceStates.CopyDest);
            cl.CopyBufferRegion(probeState, 0, upload, 0, (ulong)bytes);
            cl.ResourceBarrierTransition(probeState, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        });
        SetState(probeState, ResourceStates.NonPixelShaderResource);
        upload.Dispose();
    }

    readonly System.Collections.Generic.Dictionary<ID3D12Resource, ResourceStates> states = new();
    public ResourceStates StateOf(ID3D12Resource r) => states.TryGetValue(r, out var s) ? s : ResourceStates.UnorderedAccess;
    public void SetState(ID3D12Resource r, ResourceStates s) { states[r] = s; }

    public void SwapAndMarkHistory() { writeB = !writeB; HistoryValid = true; }

    public void ResetHistory() { HistoryValid = false; }

    public void Dispose()
    {
        irradA?.Dispose(); irradB?.Dispose(); visA?.Dispose(); visB?.Dispose(); probeState?.Dispose();
        irradA = irradB = visA = visB = probeState = null;
        Valid = false;
    }
}
