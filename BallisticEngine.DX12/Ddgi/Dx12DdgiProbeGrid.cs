using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BallisticEngine;

namespace BallisticEngine.DX12;

// DDGI probe grid — the world-space irradiance cache (the ONE durable substrate the DDGI pass owns).
//
// A uniform 3D grid of probes covers the scene's world AABB (padded one cell so surfaces on the boundary are
// bracketed). Each probe stores its diffuse irradiance in an OCTAHEDRAL cell of OctRes×OctRes float4 texels:
// irradiance[probe*OctTexels + ty*OctRes+tx]. The relight pass writes the current buffer (UAV) EMA-blended
// over the previous (SRV); the sample pass reads the current. Two buffers ping-pong by frame → a single
// world-space EMA feedback loop (NEVER pooled, the whole "radiance cache"). View-independent: no reprojection,
// no screen-space history → the entire ghosting/disocclusion class never arises.
//
// D1: irradiance only. D3 adds a parallel visibility buffer (mean depth, depth²) for the Chebyshev leak fix.
public sealed class Dx12DdgiProbeGrid : IDisposable
{
    public const int OctRes = 6;                  // octahedral cell edge (texels) per probe
    public const int OctTexels = OctRes * OctRes; // 36 float4 irradiance texels per probe

    readonly Dx12Device dev;

    public Dx12DdgiProbeGrid(Dx12Device device) { dev = device; }

    // ---- grid layout (set on Ensure; default 16x8x16 = 2048 probes) ----
    public int CountX { get; private set; }
    public int CountY { get; private set; }
    public int CountZ { get; private set; }
    public int ProbeCount => CountX * CountY * CountZ;

    // probe (ix,iy,iz) world pos = GridOrigin + (ix,iy,iz) * ProbeSpacing.
    public Vector3 GridOrigin { get; private set; }
    public Vector3 ProbeSpacing { get; private set; }

    // ---- irradiance cache (ping-pong) ----
    ID3D12Resource irradA, irradB;
    bool writeB;
    public ID3D12Resource IrradianceWrite => writeB ? irradB : irradA;
    public ID3D12Resource IrradianceRead  => writeB ? irradA : irradB;   // previous frame
    public ulong IrradianceWriteGpu => IrradianceWrite?.GPUVirtualAddress ?? 0;
    public ulong IrradianceReadGpu  => IrradianceRead?.GPUVirtualAddress ?? 0;

    public bool HistoryValid { get; private set; }
    public bool Valid { get; private set; }

    // Last-fitted layout stamp (re-fit only on a real change → static scene fits once).
    int dimStamp = -1;
    Vector3 lastMin, lastMax;

    // Fit the grid to the scene world AABB (from the scene AS instances — exactly the traced geometry) and
    // (re)allocate the irradiance buffers when the layout changes. Returns Valid.
    public bool Ensure(Dx12FrameContext ctx, int reqX, int reqY, int reqZ)
    {
        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        if (sceneAS == null || !sceneAS.Valid || sceneAS.InstanceCount == 0) { Valid = false; return false; }

        // Scene world AABB: 8-corner transform of every instance's local mesh bounds.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
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
        if (min.X > max.X) { Valid = false; return false; }   // no valid geometry

        // Layout change detector: grid dims OR a meaningful AABB shift → re-fit (and realloc if probe count grew).
        int dims = (reqX & 0x3ff) | ((reqY & 0x3ff) << 10) | ((reqZ & 0x3ff) << 20);
        bool aabbMoved = Vector3.Distance(min, lastMin) > 0.01f || Vector3.Distance(max, lastMax) > 0.01f;
        bool layoutChanged = dims != dimStamp || aabbMoved || irradA == null;

        if (layoutChanged)
        {
            // Probe spacing brackets the AABB with reqN probes; pad half a cell each side so boundary surfaces
            // sit strictly inside the grid (a surface exactly on the AABB face must still have 8 bracketing probes).
            Vector3 size = Vector3.Max(max - min, new Vector3(0.01f));
            var counts = new Vector3(MathF.Max(reqX, 2), MathF.Max(reqY, 2), MathF.Max(reqZ, 2));
            Vector3 spacing = size / (counts - Vector3.One);   // probes at both faces; (N-1) gaps span the AABB

            CountX = reqX; CountY = reqY; CountZ = reqZ;
            ProbeSpacing = spacing;
            GridOrigin = min;   // probe 0 sits at the AABB min corner; probe (N-1) at the max corner

            int needProbes = ProbeCount;
            // Realloc only when the buffer must grow (probe count is the only size driver).
            long bytes = (long)needProbes * OctTexels * 16;   // float4 = 16B
            if (irradA == null || (long)CurrentCapacityProbes * OctTexels * 16 < bytes)
            {
                ReallocIrradiance(needProbes);
                HistoryValid = false;   // fresh buffers → no usable history this frame
            }
            dimStamp = dims; lastMin = min; lastMax = max;
        }

        Valid = irradA != null;
        return Valid;
    }

    int CurrentCapacityProbes;

    void ReallocIrradiance(int probeCount)
    {
        irradA?.Dispose(); irradB?.Dispose();
        long elems = (long)probeCount * OctTexels;
        var desc = ResourceDescription.Buffer((ulong)(elems * 16), ResourceFlags.AllowUnorderedAccess);
        irradA = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        irradB = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        CurrentCapacityProbes = probeCount;
        writeB = false;
        irradAState = irradBState = ResourceStates.UnorderedAccess;
    }

    // Minimal self-tracked state for the two buffers (the relight pass transitions them each frame).
    ResourceStates irradAState, irradBState;
    public ResourceStates StateOf(ID3D12Resource r) => r == irradB ? irradBState : irradAState;
    public void SetState(ID3D12Resource r, ResourceStates s) { if (r == irradB) irradBState = s; else irradAState = s; }

    // Swap the ping-pong + mark history valid (called at the end of the relight pass).
    public void SwapAndMarkHistory() { writeB = !writeB; HistoryValid = true; }

    public void Dispose()
    {
        irradA?.Dispose(); irradB?.Dispose();
        irradA = irradB = null;
        Valid = false;
    }
}
