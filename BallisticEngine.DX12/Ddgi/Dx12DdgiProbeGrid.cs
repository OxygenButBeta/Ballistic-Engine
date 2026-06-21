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
    public const int OctRes = 8;                  // octahedral cell edge (texels) per probe (irradiance, RTXGI std)
    public const int OctTexels = OctRes * OctRes; // 64 float4 irradiance texels per probe
    public const int VisRes = 16;                 // octahedral cell edge for the visibility moments (sharper)
    public const int VisTexels = VisRes * VisRes; // 256 float2 (mean depth, mean depth²) per probe

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

    // ---- visibility moments cache (ping-pong, D3) — float2 (mean dist, mean dist²) per oct texel for the
    // Chebyshev (variance-shadow) leak test the sample pass uses to reject probes occluded from the surface. ----
    ID3D12Resource visA, visB;
    public ID3D12Resource VisibilityWrite => writeB ? visB : visA;
    public ID3D12Resource VisibilityRead  => writeB ? visA : visB;
    public ulong VisibilityWriteGpu => VisibilityWrite?.GPUVirtualAddress ?? 0;
    public ulong VisibilityReadGpu  => VisibilityRead?.GPUVirtualAddress ?? 0;

    // ---- probe state (occupancy-aware placement, set once on Ensure via GpuSceneQuery) ----
    // float4 per probe: xyz = world-space RELOCATION offset (nudge from the nominal grid position into free
    // space, so probes buried in walls/floors move out), w = active flag (1 = trace+sample, 0 = probe sits in
    // solid with nowhere to go → relight skips it, sample weights it 0 so it can't leak). NOT ping-pong: it is
    // static for a static layout (rebuilt only when the grid re-fits). Read by both relight and sample.
    ID3D12Resource probeState;
    public ID3D12Resource ProbeState => probeState;
    public ulong ProbeStateGpu => probeState?.GPUVirtualAddress ?? 0;
    public bool StatePlaced { get; private set; }   // false until the relocation pass has filled probeState

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
            if (irradA == null || CurrentCapacityProbes < needProbes)
            {
                Realloc(needProbes);
                HistoryValid = false;   // fresh buffers → no usable history this frame
            }
            dimStamp = dims; lastMin = min; lastMax = max;
            StatePlaced = false;   // layout moved → the relocation/classification result is stale, re-run it
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
        long irrBytes = (long)probeCount * OctTexels * 16;   // float4
        long visBytes = (long)probeCount * VisTexels * 8;    // float2
        irradA = MakeBuffer(irrBytes); irradB = MakeBuffer(irrBytes);
        visA = MakeBuffer(visBytes);   visB = MakeBuffer(visBytes);
        probeState = MakeBuffer((long)probeCount * 16);      // float4 (offset.xyz, active)
        CurrentCapacityProbes = probeCount;
        writeB = false;
        states.Clear();
        states[irradA] = states[irradB] = states[visA] = states[visB] = ResourceStates.UnorderedAccess;
        states[probeState] = ResourceStates.UnorderedAccess;
    }

    // Probe nominal world position (grid lattice, BEFORE relocation). The relocated position is this + offset.
    public Vector3 ProbePos(int ix, int iy, int iz) => GridOrigin + new Vector3(ix, iy, iz) * ProbeSpacing;

    // Occupancy-aware placement (run once per layout, on the CPU via GpuSceneQuery — NOT per frame). For each
    // probe: classify its lattice position; nudge probes that sit in solid out to the nearest free space; mark a
    // probe that is solid AND can't be nudged free as inactive. Fills probeState (offset.xyz, active) and uploads
    // it. `query` shares the DDGI scene AS (same TLAS the relight traces) so this needs no second AS build.
    public unsafe void PlaceProbes(Dx12Device device, GpuSceneQuery query)
    {
        if (probeState == null || StatePlaced) return;
        int n = ProbeCount;

        var pts = new Vector3[n];
        for (int iz = 0, p = 0; iz < CountZ; iz++)
            for (int iy = 0; iy < CountY; iy++)
                for (int ix = 0; ix < CountX; ix++, p++)
                    pts[p] = ProbePos(ix, iy, iz);

        // Probe radius for classify/nudge: a couple of cells reaches a wall from inside a cell without scanning
        // the whole scene (cheaper rays, and a probe should relocate to its OWN cell's free space, not far away).
        float radius = 2.0f * MathF.Max(ProbeSpacing.X, MathF.Max(ProbeSpacing.Y, ProbeSpacing.Z));

        // CPU readback (Map) — MUST run with the pipelined frame CLOSED (caller guarantees this; see RunPending
        // placement). Inside an open frame list ExecuteSync only records, so the readback would read garbage and
        // desync the fence → device removed.
        GpuSceneQuery.SpaceClass[] cls = query.ClassifySpace(pts, radius);
        Vector3[] nudged = query.NudgeToFreeSpace(pts, radius);

        // Cap the relocation. The sample gathers from the probe's MOVED position (offset is fed to the trilinear
        // bracketing), so a relocation can spill into the neighbour cell without corrupting the gather — letting a
        // probe buried deep in a thick wall slab escape to free space instead of being killed. Cap at ~1.5 cells
        // (max axis) rather than 0.9 of the SMALLEST axis: the old tight cap killed most probes in scenes with thick
        // walls / anisotropic spacing (the DGI box: min spacing 0.8 → cap 0.73, but the nudge out of a 10-unit slab
        // needs far more → every slab probe went dead → black GI). Wider cap = far fewer dead probes = real GI.
        float maxMove = 1.5f * MathF.Max(ProbeSpacing.X, MathF.Max(ProbeSpacing.Y, ProbeSpacing.Z));

        var state = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 offset = nudged[i] - pts[i];
            float move = offset.Length();
            bool solid = cls[i] == GpuSceneQuery.SpaceClass.Solid;

            if (move > maxMove)
            {
                // Can't relocate within the cell. If it was solid → dead probe (inactive). If it was merely on a
                // boundary the classify didn't flag solid, keep it active but DON'T apply the over-long move.
                offset = Vector3.Zero;
                state[i] = new Vector4(0, 0, 0, solid ? 0f : 1f);
            }
            else
            {
                // Relocate (offset) and stay active unless it was solid with a zero usable nudge.
                bool active = !(solid && move < 1e-4f);
                state[i] = new Vector4(offset.X, offset.Y, offset.Z, active ? 1f : 0f);
            }
        }

        // Upload via an upload-heap staging copy into the default-heap probeState buffer.
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

    // Self-tracked resource states (the relight pass transitions all four buffers each frame).
    readonly System.Collections.Generic.Dictionary<ID3D12Resource, ResourceStates> states = new();
    public ResourceStates StateOf(ID3D12Resource r) => states.TryGetValue(r, out var s) ? s : ResourceStates.UnorderedAccess;
    public void SetState(ID3D12Resource r, ResourceStates s) { states[r] = s; }

    // Swap the ping-pong + mark history valid (called at the end of the relight pass).
    public void SwapAndMarkHistory() { writeB = !writeB; HistoryValid = true; }

    // Invalidate the temporal history so the NEXT relight does a full replace (alpha=1) instead of EMA-blending
    // over stale data. Called by the orchestrator when GI is inactive this frame — so toggling GI off then on
    // does not bring back a stale (or runaway) cache; it rebuilds clean. Idempotent + cheap (just a flag).
    public void ResetHistory() { HistoryValid = false; }

    public void Dispose()
    {
        irradA?.Dispose(); irradB?.Dispose(); visA?.Dispose(); visB?.Dispose(); probeState?.Dispose();
        irradA = irradB = visA = visB = probeState = null;
        Valid = false;
    }
}
