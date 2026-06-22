using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using BallisticEngine;          // Mesh, MeshSdf

namespace BallisticEngine.DX12;

// Lumen FAZ 2 — the runtime GLOBAL DISTANCE FIELD (GDF).
//
// Composites every visible mesh's per-mesh SDF (Mesh.Sdf, MeshSdf — generated at import in FAZ 1) into a single
// camera-centered 3D clipmap volume texture on the GPU, the way UE5's GlobalDistanceField does. This is the field
// Lumen's software ray tracing sphere-marches against (FAZ 5). FAZ 2 only BUILDS + VISUALIZES it (debug sphere
// trace); it does not feed GI yet.
//
// Structure (v1 — ONE clip level, room left for nesting):
//   - clipmap: a persistent CLIP_RES^3 R16_Float 3D texture (cross-frame, NOT pooled). Stores the signed distance
//     (world units, clamped to ±half-extent) of the nearest surface to each voxel center. Covers a tunable cube
//     (ClipWorldExtent, default 40 m) recentered on the camera each frame, snapped to the voxel grid (no shimmer).
//   - per-mesh SDF GPU textures: each UNIQUE mesh's CPU SDF (MeshSdf.Distances) uploaded ONCE to its own
//     R16_Float Texture3D, registered in the bindless heap (so CSComposite reads it via ResourceDescriptorHeap[]).
//     Cached by Mesh identity (like BLAS caching in Dx12SceneAS).
//   - CSComposite (GlobalSdfComposite.hlsl): per clipmap voxel, transform its world center into each overlapping
//     instance's mesh-local space, trilinearly sample that mesh's SDF, MIN across overlapping instances (union of
//     solids). Instances come from the shared SceneAS (same list Lumen/Aurora enumerate); count capped + logged.
//
// TODO (FAZ 2 follow-ups, structured for but not done here):
//   - NESTED CLIPMAPS: UE uses ~4 nested levels (fine near camera, coarse far). This is one level. The constants
//     (ClipOrigin/VoxelSize/ClipRes) are already per-level shaped — add an array of clipmaps + a level select.
//   - CONSERVATIVE CULLING: v1 loops over all (capped) instances per voxel with a world-AABB reject in the shader.
//     A coarse object-grid / per-voxel instance list would scale to Bistro; today the instance count is capped.
//   - NON-UNIFORM SCALE: the mesh SDF is in mesh-local units; a non-uniformly scaled instance needs a per-axis
//     distance correction. v1 assumes ~uniform scale (true for the GI test scenes).
//
// Gated: nothing here is constructed unless the GlobalSdf door is armed (BALLISTIC_DX12_GLOBALSDF=1 or Lumen on).
public sealed class Dx12GlobalSdf : IDisposable {
    readonly Dx12Device dev;

    // ---- tunables (env-overridable) ----
    public int ClipRes { get; private set; } = 128;          // voxels per clipmap axis
    public float ClipWorldExtent { get; private set; } = 40f; // world size (m) of the clipmap cube
    int MaxInstances = 256;                                   // composite instance cap (v1; see culling TODO)

    float VoxelSize => ClipWorldExtent / ClipRes;
    float ClipHalfExtent => ClipWorldExtent * 0.5f;

    // ---- the clipmap volume (persistent) ----
    ID3D12Resource clipmap;
    ResourceStates clipmapState = ResourceStates.UnorderedAccess;
    int clipSrvIndex = -1;                   // non-shader-visible SRV (copied into the debug pass's table)
    // Persistent bindless-heap slots live in the RESERVED Global-SDF tail (Dx12BindlessTail.GlobalSdfTableBase), NOT
    // the dynamic Allocate()/Reset() cursor: the GPU-driven material table rewinds that cursor each topology change
    // (Dx12GpuDrivenRenderer.EnsureMaterialTable → BindlessHeap.Reset()), which would re-stamp these once-written slots
    // with material/geometry descriptors. The composite then reads a typed-mismatched descriptor (a 2D tex / buffer as
    // an RWTexture3D / Texture3D) → GPU page fault → DXGI_ERROR_DEVICE_REMOVED. The reserved tail is never touched by
    // the cursor, exactly like RtReflTableBase / AuroraTableBase.
    int clipUavBindless = -1;                // = GlobalSdfTableBase + 0 (the composite's u0 UAV table slot)
    int nextSdfSlot;                         // running count of unique-mesh SDF SRV slots handed out from the tail
    public CpuDescriptorHandle ClipmapSrvCpu => Dx12Backend.SrvStore.Cpu(clipSrvIndex);
    public ID3D12Resource ClipmapResource => clipmap;
    public bool Valid => clipmap != null && clipBuiltOnce;
    bool clipBuiltOnce;

    // The clipmap's current world placement (voxel-snapped origin = min corner). Read by the debug pass.
    public Vector3 ClipOrigin { get; private set; }
    public float ClipVoxelSize => VoxelSize;
    public float ClipHalf => ClipHalfExtent;

    // ---- per-mesh SDF GPU textures, cached by mesh identity ----
    sealed class MeshSdfEntry {
        public ID3D12Resource Tex;
        public int BindlessIndex;            // slot in Dx12Backend.BindlessHeap (ResourceDescriptorHeap index)
        public Vector3 GridOrigin, GridExtent;
        public float MaxLocalDist;
    }
    readonly Dictionary<Mesh, MeshSdfEntry> byMesh = new();

    // ---- composite pipeline ----
    ID3D12RootSignature compRootSig;
    ID3D12PipelineState compPso;
    Dx12FrameCb<CompositeConstants> compCb;
    ID3D12Resource instanceBuf;              // SdfInstance[] (root SRV t0)

    [StructLayout(LayoutKind.Sequential)]
    struct SdfInstance {
        public Matrix4x4 WorldToLocal;       // world → mesh-local (transposed on upload)
        public Vector3 GridOrigin; public uint SdfTexIndex;
        public Vector3 GridExtent; public float MaxLocalDist;
        public Vector3 WorldMin;  public float Pad0;
        public Vector3 WorldMax;  public float Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CompositeConstants {
        public Vector3 ClipOrigin; public float VoxelSize;
        public uint ClipResX, ClipResY, ClipResZ, InstanceCount;
        public float ClipHalfExtent; public Vector3 CompPad;
    }

    int loggedInstances = -1;
    Vector3 lastOrigin = new(float.NaN);

    public Dx12GlobalSdf(Dx12Device device) {
        dev = device;
        ReadEnvTunables();
        BuildPipeline();
    }

    void ReadEnvTunables() {
        int res = (int)EnvF("BALLISTIC_DX12_GLOBALSDF_RES", ClipRes);
        ClipRes = Math.Clamp(res, 16, 256);
        ClipWorldExtent = MathF.Max(1f, EnvF("BALLISTIC_DX12_GLOBALSDF_EXTENT", ClipWorldExtent));
        MaxInstances = Math.Clamp((int)EnvF("BALLISTIC_DX12_GLOBALSDF_MAXINST", MaxInstances), 1, 4096);
    }

    unsafe void BuildPipeline() {
        // CSComposite root sig: CBV b0 + root SRV t0 (SdfInstance[]) + table{u0 clipmap} + clamp sampler s0.
        // HeapDirectlyIndexed so the per-voxel loop reads each mesh's SDF via ResourceDescriptorHeap[SdfTexIndex].
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var uavTable = new RootParameter1(new RootDescriptorTable1(uavRange), ShaderVisibility.All);
        var clamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        compRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv, instSrv, uavTable }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/GlobalSdfComposite.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSComposite", "GlobalSdfComposite.hlsl");
        compPso = dev.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription { RootSignature = compRootSig, ComputeShader = cs });

        compCb = new Dx12FrameCb<CompositeConstants>(dev);
    }

    void EnsureClipmap() {
        if (clipmap != null) return;
        var desc = new ResourceDescription {
            Dimension = ResourceDimension.Texture3D,
            Width = (ulong)ClipRes, Height = (uint)ClipRes, DepthOrArraySize = (ushort)ClipRes, MipLevels = 1,
            // R32_Float: R16_FLOAT typed-UAV STORE is an OPTIONAL DX12 format (not in the guaranteed typed-UAV
            // set); some drivers (AMD) fault on the 3D UAV store -> device-removed. R32_Float is always typed-UAV
            // safe. (2x memory at 128^3 = 8MB, fine.)
            Format = Format.R32_Float, SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown, Flags = ResourceFlags.AllowUnorderedAccess,
        };
        clipmap = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        clipmap.Name = "GlobalSdfClipmap";
        clipmapState = ResourceStates.UnorderedAccess;

        clipSrvIndex = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(clipmap, new ShaderResourceViewDescription {
            Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(clipSrvIndex));

        // The composite's u0 (clipmap UAV) lives in the BINDLESS heap so the SAME bound shader-visible heap serves
        // both the UAV table AND the per-mesh-SDF ResourceDescriptorHeap[] reads (DX12 allows only ONE bound
        // CBV/SRV/UAV heap). A RESERVED tail slot (see clipUavBindless note above) — NOT a dynamic Allocate() (the
        // GPU-driven table re-stamp would clobber it). Written once (the resource handle never changes).
        clipUavBindless = Dx12BindlessTail.GlobalSdfTableBase + 0;
        dev.Device.CreateUnorderedAccessView(clipmap, null, new UnorderedAccessViewDescription {
            Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture3D,
            Texture3D = new Texture3DUnorderedAccessView { FirstWSlice = 0, WSize = (uint)ClipRes, MipSlice = 0 },
        }, Dx12Backend.BindlessHeap.Cpu(clipUavBindless));
    }

    // Get (or upload + cache) the per-mesh SDF GPU texture for `mesh`. Returns null when the mesh has no SDF.
    MeshSdfEntry EntryFor(Mesh mesh) {
        if (byMesh.TryGetValue(mesh, out MeshSdfEntry cached)) return cached;
        MeshSdf sdf = mesh.Sdf;
        if (sdf is null || !sdf.IsValid) {
            byMesh[mesh] = null;   // remember the negative so we don't re-check every frame
            return null;
        }

        // Out of reserved per-mesh SDF SRV slots → treat this mesh as having no SDF (don't overflow the tail into the
        // Aurora screen-probe block). The cap is generous for GI test scenes; raise GlobalSdfMaxTextures if hit.
        if (nextSdfSlot >= Dx12BindlessTail.GlobalSdfMaxTextures) {
            byMesh[mesh] = null;
            return null;
        }

        ID3D12Resource tex = UploadMeshSdf(sdf);
        int bindless = Dx12BindlessTail.GlobalSdfTableBase + 1 + nextSdfSlot++;   // reserved tail SRV slot
        dev.Device.CreateShaderResourceView(tex, new ShaderResourceViewDescription {
            Format = Format.R16_Float, ViewDimension = ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.BindlessHeap.Cpu(bindless));

        // The grid's largest representable |distance| (out-of-grid clamp value): the grid diagonal is a safe upper
        // bound on any in-grid distance.
        float maxDist = sdf.GridExtent.Length();
        var e = new MeshSdfEntry {
            Tex = tex, BindlessIndex = bindless,
            GridOrigin = sdf.GridOrigin, GridExtent = sdf.GridExtent, MaxLocalDist = maxDist,
        };
        byMesh[mesh] = e;
        return e;
    }

    // Upload a MeshSdf's float[] distances into an R16_Float Texture3D (half-converted). One-time per unique mesh.
    unsafe ID3D12Resource UploadMeshSdf(MeshSdf sdf) {
        int rx = sdf.ResX, ry = sdf.ResY, rz = sdf.ResZ;
        var desc = new ResourceDescription {
            Dimension = ResourceDimension.Texture3D,
            Width = (ulong)rx, Height = (uint)ry, DepthOrArraySize = (ushort)rz, MipLevels = 1,
            Format = Format.R16_Float, SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown, Flags = ResourceFlags.None,
        };
        ID3D12Resource dest = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties,
            HeapFlags.None, desc, ResourceStates.CopyDest);
        dest.Name = "MeshSdf3D";

        // GetCopyableFootprints handles the row-pitch alignment (256B) + slice pitch for the 3D texture.
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(desc, 0, 1, 0, footprints, rowCounts, rowSizes, out ulong totalBytes);

        // NOT `using`: this runs mid-frame (Record→Build), so dev.ExecuteSync records the copy onto the OPEN frame
        // command list and returns WITHOUT waiting for the GPU. A `using` would Dispose the upload buffer here, before
        // the GPU executes the copy → use-after-free → device-removed. Defer the release past FramesInFlight instead.
        ID3D12Resource upload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        PlacedSubresourceFootPrint fp = footprints[0];
        long rowPitch = fp.Footprint.RowPitch;                  // bytes per row (aligned)
        long slicePitch = rowPitch * ry;                        // bytes per z-slice
        byte* dst = upload.Map<byte>(0);
        float[] src = sdf.Distances;
        for (int z = 0; z < rz; z++) {
            for (int y = 0; y < ry; y++) {
                var row = (Half*)(dst + (long)fp.Offset + z * slicePitch + y * rowPitch);
                int baseIdx = sdf.Index(0, y, z);               // x-fastest: contiguous run of rx
                for (int x = 0; x < rx; x++)
                    row[x] = (Half)src[baseIdx + x];
            }
        }
        upload.Unmap(0);

        dev.ExecuteSync(cl => {
            var d = new TextureCopyLocation(dest, 0);
            var s = new TextureCopyLocation(upload, fp);
            cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            cl.ResourceBarrierTransition(dest, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        });
        dev.DeferredRelease(upload);   // keep the upload buffer alive until the GPU has run the copy
        return dest;
    }

    // Build/refresh the global SDF for this frame: recenter on the camera (voxel-snapped), gather overlapping
    // instances from the shared SceneAS, (lazily) upload their per-mesh SDFs, and run the composite. Cheap-skips
    // the recompute when the camera hasn't moved a voxel AND the scene topology is unchanged.
    public unsafe void Build(Dx12FrameContext ctx, Dx12SceneAS sceneAS, bool sceneDirty) {
        if (sceneAS is null || !sceneAS.Valid) return;
        EnsureClipmap();

        // Camera-centered, voxel-snapped origin (min corner). Snapping kills the per-frame shimmer.
        float vs = VoxelSize;
        Vector3 center = ctx.CamPos;
        Vector3 snapped = new(
            MathF.Floor(center.X / vs) * vs,
            MathF.Floor(center.Y / vs) * vs,
            MathF.Floor(center.Z / vs) * vs);
        Vector3 origin = snapped - new Vector3(ClipWorldExtent * 0.5f);

        bool moved = !(MathF.Abs(origin.X - lastOrigin.X) < vs * 0.5f
                       && MathF.Abs(origin.Y - lastOrigin.Y) < vs * 0.5f
                       && MathF.Abs(origin.Z - lastOrigin.Z) < vs * 0.5f);
        if (clipBuiltOnce && !moved && !sceneDirty)
            return;   // nothing changed enough to rebuild the field

        ClipOrigin = origin;
        lastOrigin = origin;

        // Gather overlapping instances (cap to MaxInstances). The clipmap world AABB.
        Vector3 clipMin = origin;
        Vector3 clipMax = origin + new Vector3(ClipWorldExtent);
        var records = new List<SdfInstance>(Math.Min(sceneAS.InstanceCount, MaxInstances));
        int withSdf = 0, skippedNoSdf = 0;
        for (int i = 0; i < sceneAS.InstanceCount && records.Count < MaxInstances; i++) {
            Mesh mesh = sceneAS.InstanceMesh(i);
            MeshSdfEntry e = EntryFor(mesh);
            if (e is null) { skippedNoSdf++; continue; }

            Matrix4x4 world = sceneAS.InstanceWorld(i);
            // Instance SDF world AABB = the mesh grid box transformed to world (8 corners).
            ComputeWorldAabb(world, e.GridOrigin, e.GridExtent, out Vector3 wMin, out Vector3 wMax);
            // Skip instances whose SDF band doesn't overlap the clipmap at all.
            if (wMax.X < clipMin.X || wMin.X > clipMax.X ||
                wMax.Y < clipMin.Y || wMin.Y > clipMax.Y ||
                wMax.Z < clipMin.Z || wMin.Z > clipMax.Z)
                continue;

            if (!Matrix4x4.Invert(world, out Matrix4x4 worldToLocal))
                worldToLocal = Matrix4x4.Identity;
            records.Add(new SdfInstance {
                WorldToLocal = Matrix4x4.Transpose(worldToLocal),   // HLSL column-major
                GridOrigin = e.GridOrigin, SdfTexIndex = (uint)e.BindlessIndex,
                GridExtent = e.GridExtent, MaxLocalDist = e.MaxLocalDist,
                WorldMin = wMin, WorldMax = wMax,
            });
            withSdf++;
        }

        if (records.Count == 0) {
            // No SDF-carrying instances overlap → fill the clipmap with "far" so the debug trace shows nothing
            // (rather than stale geometry). Cheap: a single clear-style composite with InstanceCount=0.
            RunComposite(origin, Array.Empty<SdfInstance>());
            clipBuiltOnce = true;
            LogOnce(0, skippedNoSdf, sceneAS.InstanceCount);
            return;
        }

        RunComposite(origin, records.ToArray());
        clipBuiltOnce = true;
        LogOnce(withSdf, skippedNoSdf, sceneAS.InstanceCount);
    }

    unsafe void RunComposite(Vector3 origin, SdfInstance[] records) {
        dev.DeferredRelease(instanceBuf);
        instanceBuf = records.Length > 0
            ? dev.CreateUavBuffer<SdfInstance>(records, ResourceStates.GenericRead)
            : null;

        compCb.Write(new CompositeConstants {
            ClipOrigin = origin, VoxelSize = VoxelSize,
            ClipResX = (uint)ClipRes, ClipResY = (uint)ClipRes, ClipResZ = (uint)ClipRes,
            InstanceCount = (uint)records.Length, ClipHalfExtent = ClipHalfExtent,
        });

        ulong cbAddr = compCb.Gpu;
        ulong instAddr = instanceBuf?.GPUVirtualAddress ?? 0;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        GpuDescriptorHandle uavGpu = bindless.Gpu(clipUavBindless);   // clipmap UAV table → bindless heap slot
        int groups = (ClipRes + 3) / 4;

        dev.ExecuteSync(cl => {
            if (clipmapState != ResourceStates.UnorderedAccess) {
                cl.ResourceBarrierTransition(clipmap, clipmapState, ResourceStates.UnorderedAccess);
                clipmapState = ResourceStates.UnorderedAccess;
            }
            // The composite reads per-mesh SDFs via ResourceDescriptorHeap[] (HeapDirectlyIndexed) AND the clipmap
            // UAV table — both index the SAME (single allowed) bound CBV/SRV/UAV heap: the bindless heap.
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(compRootSig);
            cl.SetPipelineState(compPso);
            cl.SetComputeRootConstantBufferView(0, cbAddr);
            if (instAddr != 0)
                cl.SetComputeRootShaderResourceView(1, instAddr);
            cl.SetComputeRootDescriptorTable(2, uavGpu);
            cl.Dispatch((uint)groups, (uint)groups, (uint)groups);
            cl.ResourceBarrierTransition(clipmap, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            clipmapState = ResourceStates.NonPixelShaderResource;
        });
    }

    // Transition the clipmap to a pixel-shader-readable state (the debug fullscreen pass reads it as a PS SRV).
    public void ToPixelShaderResource() {
        if (clipmap == null || clipmapState == ResourceStates.PixelShaderResource) return;
        ResourceStates from = clipmapState;
        dev.ExecuteSync(cl => cl.ResourceBarrierTransition(clipmap, from, ResourceStates.PixelShaderResource));
        clipmapState = ResourceStates.PixelShaderResource;
    }

    static void ComputeWorldAabb(Matrix4x4 world, Vector3 gridOrigin, Vector3 gridExtent,
                                 out Vector3 wMin, out Vector3 wMax) {
        wMin = new Vector3(float.MaxValue);
        wMax = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++) {
            var local = new Vector3(
                gridOrigin.X + ((c & 1) != 0 ? gridExtent.X : 0f),
                gridOrigin.Y + ((c & 2) != 0 ? gridExtent.Y : 0f),
                gridOrigin.Z + ((c & 4) != 0 ? gridExtent.Z : 0f));
            Vector3 w = Vector3.Transform(local, world);
            wMin = Vector3.Min(wMin, w);
            wMax = Vector3.Max(wMax, w);
        }
    }

    void LogOnce(int withSdf, int skippedNoSdf, int totalInstances) {
        if (loggedInstances == withSdf) return;
        loggedInstances = withSdf;
        string line = $"[GlobalSdf] clipmap {ClipRes}^3 extent={ClipWorldExtent:0.#}m voxel={VoxelSize:0.###}m " +
                      $"instances: sdf={withSdf} noSdf={skippedNoSdf} total={totalInstances} cap={MaxInstances} " +
                      $"meshTextures={CountRealEntries()}";
        Console.WriteLine(line);
        Debugging.Log(line);
    }

    int CountRealEntries() {
        int n = 0;
        foreach (MeshSdfEntry e in byMesh.Values) if (e != null) n++;
        return n;
    }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    public void Dispose() {
        foreach (MeshSdfEntry e in byMesh.Values) e?.Tex?.Dispose();
        byMesh.Clear();
        instanceBuf?.Dispose(); instanceBuf = null;
        clipmap?.Dispose(); clipmap = null;
        compPso?.Dispose(); compRootSig?.Dispose(); compCb?.Dispose();
    }
}
