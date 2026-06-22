using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using BallisticEngine;          // IStaticMeshRenderer, RuntimeSet

namespace BallisticEngine.DX12;

// Lumen (UE5-style) — the SCENE SUBSTRATE for the Lumen GI stack.
//
// FAZ 0 (THIS milestone — scaffold only): this is the MINIMAL durable scene representation Lumen reads. It is
// NOT a pass and runs NO shading. Modeled closely on Dx12AuroraScene but TRIMMED to the bare minimum a FAZ-0
// scaffold needs:
//   - shares the BLAS/TLAS (Dx12SceneAS) reached through the shared DXR holder (ctx.Dxr) — Lumen does NOT build
//     its own AS, it reuses the one RT shadows / reflections / Aurora already maintain (stamp-cached: a static
//     scene builds once).
//   - per-instance META {instance index + world matrix + global triangle offset/count} — enough to address an
//     instance's geometry; built in the SAME instance order as the SceneAS so one index hits both.
//   - a TOPOLOGY stamp (object count + per-instance tri counts) and a TRANSFORM stamp (per-instance world
//     matrices) for dirty detection: the meta is rebuilt only when topology changes, and world matrices are
//     re-uploaded only when something actually moved (a static scene pays 0).
//
// FAZ 1-3 EXTEND this substrate with the data Lumen actually needs and which is DELIBERATELY ABSENT here:
//   - FAZ 1: per-mesh SDF (signed distance field) references + the global SDF atlas for software ray tracing.
//   - FAZ 2/3: mesh-CARD records (the planar surface cards Lumen unwraps each mesh into) + the SURFACE CACHE
//     atlas (the lit, view-independent radiance the screen probes gather from).
// None of those buffers exist yet — FAZ 0 holds ONLY the TLAS reference + instance meta + dirty stamps.
//
// Gated behind BALLISTIC_DX12_LUMEN: default-off = nothing allocated, byte-identical to a no-Lumen frame.
public sealed class Dx12LumenScene : IDisposable
{
    readonly Dx12Device dev;

    // ---- per-instance meta (matches the trimmed FAZ-0 shape; FAZ 1-3 add bindless geo + card/SDF indices) ----
    // Same instance order as Dx12SceneAS / Dx12RtGeometry so one index addresses all three. FAZ 0 carries only
    // what a scaffold needs: the global triangle range + the world matrix (object→world).
    [StructLayout(LayoutKind.Sequential)]
    struct LumenInstanceMeta
    {
        public uint TriOffset; public uint TriCount; public uint Pad0; public uint Pad1;
        public Matrix4x4 World;   // object→world, transposed on upload (HLSL column-major)
    }

    ID3D12Resource instanceMeta;        // LumenInstanceMeta[] — root SRV, indexed by instance
    public ulong InstanceMetaGpuAddress => instanceMeta?.GPUVirtualAddress ?? 0;
    public int InstanceCount { get; private set; }
    public int TotalTriangles { get; private set; }

    // ---- dirty tracking ----
    // TOPOLOGY stamp: object count + per-instance triangle counts. A change means the meta layout is stale →
    // full rebuild. Deliberately EXCLUDES transforms (a moving instance keeps the same layout — only its world
    // matrix needs re-uploading), mirroring the Aurora split so play-mode motion doesn't realloc every frame.
    int topologyStamp = -1;
    int transformStamp = -1;
    public bool DirtyThisFrame { get; private set; }   // true on a frame the meta was (re)built
    bool loggedThisStamp;

    public Dx12LumenScene(Dx12Device device) { dev = device; }

    public bool Valid => InstanceCount > 0 && instanceMeta != null;

    // Refresh the substrate for this frame: ensure the shared TLAS, rebuild the per-instance meta on a topology
    // change, re-upload world matrices on a transform change, log the counts once per stamp. Returns usability.
    public bool Ensure(Dx12FrameContext ctx)
    {
        DirtyThisFrame = false;

        if (!ctx.Dxr.CheckAvailable("Lumen"))
            return false;

        Dx12SceneAS sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid)
            return false;

        int objects = sceneAS.InstanceCount;
        int s = ComputeTopologyStamp(sceneAS, objects);
        if (s != topologyStamp || instanceMeta == null)
        {
            topologyStamp = s;
            Rebuild(sceneAS);
            DirtyThisFrame = true;
            loggedThisStamp = false;
        }
        else
        {
            int ts = ComputeTransformStamp(sceneAS);
            if (ts != transformStamp)
            {
                transformStamp = ts;
                RefreshTransforms(sceneAS);
            }
        }

        if (!loggedThisStamp)
        {
            loggedThisStamp = true;
            string line = $"[Lumen] scene: objects={InstanceCount} tris={TotalTriangles} (FAZ 0 substrate — " +
                          "TLAS shared, instance meta built; SDF/cards/surface-cache come in FAZ 1-3)";
            Console.WriteLine(line);
            Debugging.Log(line);
        }

        return Valid;
    }

    // Build the per-instance meta (prefix sum of tri counts + world matrix). FAZ 1-3 append bindless geo indices,
    // SDF refs, and mesh-card record offsets here.
    void Rebuild(Dx12SceneAS sceneAS)
    {
        int n = sceneAS.InstanceCount;
        InstanceCount = n;

        var meta = BuildMetaArray(sceneAS, out int total);
        TotalTriangles = total;

        instanceMeta?.Dispose();
        instanceMeta = n > 0 ? dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead) : null;

        // Rebuild already uploaded THIS frame's transforms — record their stamp so the next frame doesn't fire a
        // redundant RefreshTransforms (which would re-upload identical matrices). Mirrors the Aurora fix.
        transformStamp = ComputeTransformStamp(sceneAS);
    }

    // Re-upload only the per-instance world matrices (topology unchanged). Cheap (a handful of instances).
    void RefreshTransforms(Dx12SceneAS sceneAS)
    {
        if (sceneAS.InstanceCount == 0) return;
        var meta = BuildMetaArray(sceneAS, out _);
        instanceMeta?.Dispose();
        instanceMeta = dev.CreateUavBuffer<LumenInstanceMeta>(meta, ResourceStates.GenericRead);
    }

    LumenInstanceMeta[] BuildMetaArray(Dx12SceneAS sceneAS, out int total)
    {
        int n = sceneAS.InstanceCount;
        var meta = new LumenInstanceMeta[Math.Max(n, 1)];
        int offset = 0;
        for (int i = 0; i < n; i++)
        {
            int tris = sceneAS.InstanceTriangleCount(i);
            meta[i] = new LumenInstanceMeta
            {
                TriOffset = (uint)offset, TriCount = (uint)tris,
                World = Matrix4x4.Transpose(sceneAS.InstanceWorld(i)),
            };
            offset += tris;
        }
        total = offset;
        return meta;
    }

    // TOPOLOGY-only stamp: object count + per-instance triangle counts. Excludes transforms (a moving instance
    // keeps the same layout → no rebuild, just RefreshTransforms).
    int ComputeTopologyStamp(Dx12SceneAS sceneAS, int objects)
    {
        var h = new HashCode();
        h.Add(objects);
        for (int i = 0; i < sceneAS.InstanceCount; i++)
            h.Add(sceneAS.InstanceTriangleCount(i));
        return h.ToHashCode();
    }

    // A cheap stamp of all instance WORLD matrices (full upper-left 3×3 + translation) — RefreshTransforms runs
    // only when this changes. On a static scene it never changes → RefreshTransforms is skipped entirely.
    int ComputeTransformStamp(Dx12SceneAS sceneAS)
    {
        var h = new HashCode();
        for (int i = 0; i < sceneAS.InstanceCount; i++)
        {
            Matrix4x4 w = sceneAS.InstanceWorld(i);
            h.Add(w.M11); h.Add(w.M12); h.Add(w.M13);
            h.Add(w.M21); h.Add(w.M22); h.Add(w.M23);
            h.Add(w.M31); h.Add(w.M32); h.Add(w.M33);
            h.Add(w.M41); h.Add(w.M42); h.Add(w.M43);   // translation
        }
        return h.ToHashCode();
    }

    public void Dispose()
    {
        instanceMeta?.Dispose(); instanceMeta = null;
    }
}
