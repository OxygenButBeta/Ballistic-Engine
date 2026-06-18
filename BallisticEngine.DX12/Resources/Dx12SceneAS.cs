using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using GLMatrix4 = System.Numerics.Matrix4x4;   // engine math is System.Numerics now
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

// Scene ray-tracing acceleration structures for the DXR effects (shadows/reflections/GI). Builds one BLAS
// per unique mesh (over its whole position/index buffer — opaque, no per-submesh split needed for tracing)
// and a TLAS over one instance per renderer (world matrix). Cached by a geometry stamp: a static scene
// builds once; the TLAS rebuilds (cheap) when transforms/instances change, BLAS only when a new mesh
// appears. Both SunTemple + Bistro are single whole-mesh renderers → 1 BLAS + 1 instance. The TLAS SRV
// (a null-resource RaytracingAccelerationStructure view) is what the RT passes bind.
public sealed class Dx12SceneAS : IDisposable {
    readonly Dx12Device dev;
    readonly ID3D12Device5 device5;

    readonly Dictionary<Mesh, ID3D12Resource> blasByMesh = new();   // cached per mesh (never rebuilt)
    ID3D12Resource tlas;
    int stamp = -1;

    public ulong TlasAddress => tlas?.GPUVirtualAddress ?? 0;
    public bool Valid => tlas != null;

    public Dx12SceneAS(Dx12Device device) {
        dev = device;
        device5 = dev.Device.QueryInterface<ID3D12Device5>();
    }

    readonly List<(Mesh mesh, Matrix4x4 world)> instances = new();

    // Rebuild the AS if the scene geometry/instances changed since last frame (stamp compare). Cheap no-op
    // for a static scene after the first build.
    public void Ensure(IEnumerable<IStaticMeshRenderer> renderers) {
        instances.Clear();
        var h = new HashCode();
        foreach (IStaticMeshRenderer r in renderers) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh?.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
            if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
            Matrix4x4 world = ToNum(r.Transform.WorldMatrix);
            instances.Add((mesh, world));
            h.Add(mesh.GetHashCode());
            AddMatrix(ref h, world);
        }
        int s = h.ToHashCode();
        if (s == stamp && tlas != null) return;
        stamp = s;
        Build();
    }

    unsafe void Build() {
        if (instances.Count == 0) { tlas?.Dispose(); tlas = null; return; }

        // 1. Ensure a BLAS exists for every unique mesh (build the missing ones).
        var toBuild = new List<(Mesh mesh, BuildRaytracingAccelerationStructureInputs inputs, ID3D12Resource result, ID3D12Resource scratch)>();
        foreach (var (mesh, _) in instances) {
            if (blasByMesh.ContainsKey(mesh)) continue;
            var vb = (Dx12Buffer<GLVector3>)mesh.VertexBuffer;
            var ib = (Dx12IndexBuffer)mesh.IndexBuffer;
            var geom = new RaytracingGeometryDescription {
                Type = RaytracingGeometryType.Triangles, Flags = RaytracingGeometryFlags.Opaque,
                Triangles = new RaytracingGeometryTrianglesDescription {
                    VertexBuffer = new GpuVirtualAddressAndStride(vb.GpuAddress, (ulong)vb.Stride),
                    VertexFormat = Format.R32G32B32_Float, VertexCount = (uint)vb.ElementCount,
                    IndexBuffer = ib.GpuAddress, IndexFormat = Format.R32_UInt, IndexCount = (uint)ib.ElementCount,
                    Transform3x4 = 0,
                },
            };
            var inputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.BottomLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = 1, GeometryDescriptions = new[] { geom },
            };
            var pre = device5.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
            ID3D12Resource result = AsBuffer(pre.ResultDataMaxSizeInBytes, ResourceStates.RaytracingAccelerationStructure);
            ID3D12Resource scratch = AsBuffer(pre.ScratchDataSizeInBytes, ResourceStates.UnorderedAccess);
            blasByMesh[mesh] = result;
            toBuild.Add((mesh, inputs, result, scratch));
        }

        // 2. Instance descriptors (one per renderer, world matrix → DXR 3x4 row-major instance-to-world).
        int instSize = Marshal.SizeOf<RaytracingInstanceDescription>();
        ID3D12Resource instBuf = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer((ulong)((long)instSize * instances.Count)), ResourceStates.GenericRead);
        byte* ip = instBuf.Map<byte>(0);
        for (int i = 0; i < instances.Count; i++) {
            var (mesh, world) = instances[i];
            var inst = new RaytracingInstanceDescription {
                Transform = ToDxrTransform(world), InstanceMask = 0xFF,
                AccelerationStructure = blasByMesh[mesh].GPUVirtualAddress,
            };
            Marshal.StructureToPtr(inst, (IntPtr)(ip + (long)i * instSize), false);
        }
        instBuf.Unmap(0);

        var tlasInputs = new BuildRaytracingAccelerationStructureInputs {
            Type = RaytracingAccelerationStructureType.TopLevel, Layout = ElementsLayout.Array,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            DescriptorsCount = (uint)instances.Count, InstanceDescriptions = instBuf.GPUVirtualAddress,
        };
        var tlasPre = device5.GetRaytracingAccelerationStructurePrebuildInfo(tlasInputs);
        ID3D12Resource newTlas = AsBuffer(tlasPre.ResultDataMaxSizeInBytes, ResourceStates.RaytracingAccelerationStructure);
        ID3D12Resource tlasScratch = AsBuffer(tlasPre.ScratchDataSizeInBytes, ResourceStates.UnorderedAccess);

        // 3. Record all builds (BLAS first, UAV barriers, then TLAS) in one submission.
        // MUST be ExecuteSyncImmediate (submit + WaitForGpu NOW), NOT ExecuteSync: under the pipelined frame
        // (P0a, default) ExecuteSync only RECORDS into the open frame list and returns without submitting — the
        // build runs at EndFrame. But the scratch + instance buffers are disposed IMMEDIATELY below, so the
        // deferred GPU build would read/write FREED memory → invalid AS → GPU HANG (DEVICE_HUNG, PageFaultVA=0,
        // reproduced on the RX 9070 XT for RT-GI / RT-shadows). Immediate completes the build before we free its
        // transient inputs. This is a once-per-stamp cost (static scene = first frame only), so the synchronous
        // flush is fine — the AS must exist before any RT dispatch this frame anyway.
        dev.ExecuteSyncImmediate(cl => {
            foreach (var b in toBuild) {
                cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                    Inputs = b.inputs, DestinationAccelerationStructureData = b.result.GPUVirtualAddress,
                    ScratchAccelerationStructureData = b.scratch.GPUVirtualAddress,
                });
                cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(b.result)));
            }
            cl.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription {
                Inputs = tlasInputs, DestinationAccelerationStructureData = newTlas.GPUVirtualAddress,
                ScratchAccelerationStructureData = tlasScratch.GPUVirtualAddress,
            });
            cl.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(newTlas)));
        });

        foreach (var b in toBuild) b.scratch.Dispose();
        tlasScratch.Dispose();
        instBuf.Dispose();
        tlas?.Dispose();
        tlas = newTlas;
    }

    // Create a shader-visible AS SRV (null-resource RaytracingAccelerationStructure view) at `dst`.
    public void CreateTlasSrv(CpuDescriptorHandle dst) {
        dev.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription {
            Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView { Location = tlas.GPUVirtualAddress },
        }, dst);
    }

    ID3D12Resource AsBuffer(ulong size, ResourceStates state) =>
        dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(size, ResourceFlags.AllowUnorderedAccess), state);

    // System.Numerics world (row-vector, translation in M41..M43) → DXR Matrix3x4 (row-major, column-vector
    // instance-to-world) = transpose of the upper 3 rows.
    static Matrix3x4 ToDxrTransform(Matrix4x4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43);

    static void AddMatrix(ref HashCode h, Matrix4x4 m) {
        h.Add(m.M11); h.Add(m.M12); h.Add(m.M13); h.Add(m.M14);
        h.Add(m.M21); h.Add(m.M22); h.Add(m.M23); h.Add(m.M24);
        h.Add(m.M31); h.Add(m.M32); h.Add(m.M33); h.Add(m.M34);
        h.Add(m.M41); h.Add(m.M42); h.Add(m.M43); h.Add(m.M44);
    }

    static Matrix4x4 ToNum(GLMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    public void Dispose() {
        foreach (var b in blasByMesh.Values) b.Dispose();
        blasByMesh.Clear();
        tlas?.Dispose(); tlas = null;
        device5.Dispose();
    }
}
