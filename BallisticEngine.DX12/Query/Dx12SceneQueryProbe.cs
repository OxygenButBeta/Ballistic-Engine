using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BallisticEngine.DX12;

// Self-test for GpuSceneQuery (run with BALLISTIC_DX12_SCENEQUERY_TEST=1). Builds a KNOWN closed box
// (a unit cube, -1..+1, a tiny "room/wall") as a BLAS+TLAS, injects it into a GpuSceneQuery, and checks the
// three primitives against ground truth:
//   - occupancy: a point at the box centre is INSIDE (odd ray-parity); a point far outside is FREE
//   - visibility: a pair straddling the box (inside <-> far outside) is BLOCKED; a pair in clear open
//     space (well away from the box) is VISIBLE
//   - classify: the centre is SOLID; a far-away open point is OPEN
//   - determinism: running occupancy twice returns byte-identical results
// Proves the inline-RayQuery shader logic + the dispatch/readback plumbing in isolation, with NO scene or
// renderers (the box AS is built here), before wiring the real Dx12SceneAS scene path. Mirrors Dx12DxrProbe.
public static class Dx12SceneQueryProbe {
    public static unsafe bool SelfTest(Dx12Device dev) {
        bool ok = true;
        ID3D12Resource vb = null, ib = null, blas = null, blasScratch = null, tlas = null, tlasScratch = null, instBuf = null;
        GpuSceneQuery query = null;
        try {
            var opt5 = dev.Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
            if (opt5.RaytracingTier < RaytracingTier.Tier1_1) {
                Console.WriteLine($"[SceneQueryTest] SKIP: inline RayQuery needs Tier 1.1 (have {opt5.RaytracingTier})");
                return true;   // not a failure on hardware without it
            }
            using ID3D12Device5 device5 = dev.Device.QueryInterface<ID3D12Device5>();

            ID3D12Resource Buf(ulong size, ResourceFlags f, ResourceStates st, HeapProperties heap) =>
                dev.Device.CreateCommittedResource(heap, HeapFlags.None, ResourceDescription.Buffer(size, f), st);

            // --- A unit cube (-1..+1): 8 corners, 12 triangles, indexed. Watertight & closed. ---
            float[] verts = {
                -1,-1,-1,  1,-1,-1,  1,1,-1,  -1,1,-1,   // back (z=-1)
                -1,-1, 1,  1,-1, 1,  1,1, 1,  -1,1, 1,   // front (z=+1)
            };
            uint[] idx = {
                0,1,2, 0,2,3,       // back
                4,6,5, 4,7,6,       // front
                0,4,5, 0,5,1,       // bottom
                3,2,6, 3,6,7,       // top
                0,3,7, 0,7,4,       // left
                1,5,6, 1,6,2,       // right
            };
            vb = Buf((ulong)(verts.Length * 4), ResourceFlags.None, ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            { byte* p = vb.Map<byte>(0); fixed (float* s = verts) Unsafe.CopyBlock(p, s, (uint)(verts.Length * 4)); vb.Unmap(0); }
            ib = Buf((ulong)(idx.Length * 4), ResourceFlags.None, ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            { byte* p = ib.Map<byte>(0); fixed (uint* s = idx) Unsafe.CopyBlock(p, s, (uint)(idx.Length * 4)); ib.Unmap(0); }

            var geom = new RaytracingGeometryDescription {
                Type = RaytracingGeometryType.Triangles, Flags = RaytracingGeometryFlags.Opaque,
                Triangles = new RaytracingGeometryTrianglesDescription {
                    VertexBuffer = new GpuVirtualAddressAndStride(vb.GPUVirtualAddress, 12),
                    VertexFormat = Format.R32G32B32_Float, VertexCount = 8,
                    IndexBuffer = ib.GPUVirtualAddress, IndexFormat = Format.R32_UInt, IndexCount = (uint)idx.Length,
                    Transform3x4 = 0,
                },
            };
            var blasInputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.BottomLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = 1, GeometryDescriptions = new[] { geom },
            };
            var blasPre = device5.GetRaytracingAccelerationStructurePrebuildInfo(blasInputs);
            blasScratch = Buf(blasPre.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess, HeapProperties.DefaultHeapProperties);
            blas = Buf(blasPre.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure, HeapProperties.DefaultHeapProperties);

            var inst = new RaytracingInstanceDescription {
                Transform = new Matrix3x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0),
                InstanceMask = 0xFF, AccelerationStructure = blas.GPUVirtualAddress,
            };
            instBuf = Buf((ulong)Marshal.SizeOf<RaytracingInstanceDescription>(), ResourceFlags.None, ResourceStates.GenericRead, HeapProperties.UploadHeapProperties);
            { byte* p = instBuf.Map<byte>(0); Marshal.StructureToPtr(inst, (IntPtr)p, false); instBuf.Unmap(0); }

            var tlasInputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.TopLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = 1, InstanceDescriptions = instBuf.GPUVirtualAddress,
            };
            var tlasPre = device5.GetRaytracingAccelerationStructurePrebuildInfo(tlasInputs);
            tlasScratch = Buf(tlasPre.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess, HeapProperties.DefaultHeapProperties);
            tlas = Buf(tlasPre.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure, HeapProperties.DefaultHeapProperties);

            dev.ExecuteSync(cl => {
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                    Inputs = blasInputs, DestinationAccelerationStructureData = blas.GPUVirtualAddress,
                    ScratchAccelerationStructureData = blasScratch.GPUVirtualAddress,
                });
                cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(blas)));
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                    Inputs = tlasInputs, DestinationAccelerationStructureData = tlas.GPUVirtualAddress,
                    ScratchAccelerationStructureData = tlasScratch.GPUVirtualAddress,
                });
                cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(tlas)));
            });

            ulong tlasAddr = tlas.GPUVirtualAddress;
            query = new GpuSceneQuery(dev);
            query.SetTestTlasWriter(dst => dev.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription {
                Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = tlasAddr },
            }, dst));

            var inside  = new Vector3(0, 0, 0);     // box centre -> occupied
            var outside = new Vector3(50, 0, 0);    // far outside -> free
            var farOpen = new Vector3(100, 100, 0); // open space, away from the box

            // occupancy
            bool[] occ = query.OccupancyAt(new[] { inside, outside });
            ok &= Check("occupancy: centre is inside", occ[0] == true);
            ok &= Check("occupancy: far point is free", occ[1] == false);

            // determinism: a second run is byte-identical
            bool[] occ2 = query.OccupancyAt(new[] { inside, outside });
            ok &= Check("occupancy: deterministic (run 2 == run 1)", occ2[0] == occ[0] && occ2[1] == occ[1]);

            // visibility
            bool[] vis = query.Visibility(new[] {
                (inside, outside),                              // through the box wall -> blocked
                (new Vector3(50, 0, 0), new Vector3(50, 50, 0)) // clear open space -> visible
            });
            ok &= Check("visibility: across the box wall is blocked", vis[0] == false);
            ok &= Check("visibility: clear open line of sight", vis[1] == true);

            // classify
            var cls = query.ClassifySpace(new[] { inside, farOpen });
            ok &= Check("classify: centre is Solid", cls[0] == GpuSceneQuery.SpaceClass.Solid);
            ok &= Check("classify: far point is Open", cls[1] == GpuSceneQuery.SpaceClass.Open);

            // nudge: the box centre is moved OUT to free space (no longer occupied).
            Vector3[] nudged = query.NudgeToFreeSpace(new[] { inside });
            bool[] nudgedOcc = query.OccupancyAt(new[] { nudged[0] });
            ok &= Check($"nudge: centre moved to free space ({nudged[0].X:0.##},{nudged[0].Y:0.##},{nudged[0].Z:0.##})",
                nudgedOcc[0] == false && nudged[0] != inside);

            // visibility clusters: two points on the +X side of the box vs two on the -X side, kept inside the
            // box's y-extent (|y|<=0.5) so every CROSS-side sightline passes through the cube and is blocked,
            // while each same-side vertical pair has clear LOS. -> exactly 2 rooms.
            var roomPts = new[] {
                new Vector3( 3, 0, 0), new Vector3( 3, 0.5f, 0),   // +X side
                new Vector3(-3, 0, 0), new Vector3(-3, 0.5f, 0),   // -X side
            };
            int[] rooms = query.VisibilityClusters(roomPts);
            bool twoRooms = rooms[0] == rooms[1] && rooms[2] == rooms[3] && rooms[0] != rooms[2];
            ok &= Check($"clusters: +X and -X sides are 2 separate rooms (labels {rooms[0]},{rooms[1]},{rooms[2]},{rooms[3]})", twoRooms);
        } catch (Exception e) {
            ok = false;
            Console.WriteLine($"[SceneQueryTest] FAIL (exception): {e.Message}\n{e.StackTrace}");
            try { Console.WriteLine(dev.DrainDebugMessages()); } catch { }
        } finally {
            query?.Dispose();
            instBuf?.Dispose(); tlasScratch?.Dispose(); tlas?.Dispose();
            blasScratch?.Dispose(); blas?.Dispose(); ib?.Dispose(); vb?.Dispose();
        }
        Console.WriteLine(ok
            ? "[SceneQueryTest] PASS: occupancy + visibility + classify + determinism verified."
            : "[SceneQueryTest] FAIL: see checks above.");
        return ok;
    }

    static bool Check(string label, bool cond) {
        Console.WriteLine($"  [{(cond ? "ok" : "XX")}] {label}");
        return cond;
    }
}
