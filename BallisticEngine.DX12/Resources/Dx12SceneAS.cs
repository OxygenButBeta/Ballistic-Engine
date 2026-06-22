using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using GLMatrix4 = System.Numerics.Matrix4x4;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

public sealed class Dx12SceneAS : IDisposable {
    readonly Dx12Device dev;
    readonly ID3D12Device5 device5;

    readonly Dictionary<Mesh, ID3D12Resource> blasByMesh = new();
    ID3D12Resource tlas;
    int stamp = -1;

    public ulong TlasAddress => tlas?.GPUVirtualAddress ?? 0;
    public bool Valid => tlas != null;

    public int InstanceCount => instances.Count;
    public Matrix4x4 InstanceWorld(int i) => instances[i].world;
    public int InstanceTriangleCount(int i) => instances[i].mesh.IndexBuffer.ElementCount / 3;

    public Mesh InstanceMesh(int i) => instances[i].mesh;

    // FAZ 3c: the renderer behind each instance (same order as InstanceMesh/InstanceWorld). The Lumen card capture
    // needs it to resolve per-submesh materials (MaterialFor), which the bare Mesh doesn't carry. Null-safe.
    public IStaticMeshRenderer InstanceRenderer(int i) => instances[i].renderer;

    public Dx12SceneAS(Dx12Device device) {
        dev = device;
        device5 = dev.Device.QueryInterface<ID3D12Device5>();
    }

    readonly List<(Mesh mesh, Matrix4x4 world, IStaticMeshRenderer renderer)> instances = new();

    public void Ensure(IEnumerable<IStaticMeshRenderer> renderers) {
        instances.Clear();
        var h = new HashCode();
        foreach (IStaticMeshRenderer r in renderers) {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            Mesh mesh = r.SharedMesh;
            if (mesh?.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
            if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
            Matrix4x4 world = ToNum(r.Transform.WorldMatrix);
            instances.Add((mesh, world, r));
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

        var toBuild = new List<(Mesh mesh, BuildRaytracingAccelerationStructureInputs inputs, ID3D12Resource result, ID3D12Resource scratch)>();
        foreach (var (mesh, _, _) in instances) {
            if (blasByMesh.ContainsKey(mesh)) continue;
            var vb = (Dx12Buffer<GLVector3>)mesh.VertexBuffer;
            var ib = (Dx12IndexBuffer)mesh.IndexBuffer;
            var vbAddr = new GpuVirtualAddressAndStride(vb.GpuAddress, (ulong)vb.Stride);

            // Build the BLAS from the LOD0 index range of each submesh ONLY. The mesh's index buffer packs every
            // LOD level back-to-back (LodChainBuilder appends the decimated chains after the full-res indices), so
            // feeding the WHOLE buffer (ib.ElementCount) put the coarse, volume-inflated LOD meshes into the ray-
            // tracing scene on top of LOD0 — a phantom solid hovering over the real surface. Screen-space render
            // draws LOD0 (looks fine), but RTAO/GI rays hit the invisible coarse hull and report skyVis≈0, painting
            // hard black blotches on open ground. SubMeshData.IndexStart/IndexCount is the LOD0 slice; coarse LODs
            // live only in sub.Lods[1..] and are deliberately excluded here. No LODs → one geom over the full range
            // (byte-identical to before).
            var geoms = new List<RaytracingGeometryDescription>(Math.Max(mesh.SubMeshes.Length, 1));
            foreach (SubMeshData sub in mesh.SubMeshes) {
                if (sub.IndexCount <= 0) continue;
                geoms.Add(new RaytracingGeometryDescription {
                    Type = RaytracingGeometryType.Triangles, Flags = RaytracingGeometryFlags.Opaque,
                    Triangles = new RaytracingGeometryTrianglesDescription {
                        VertexBuffer = vbAddr,
                        VertexFormat = Format.R32G32B32_Float, VertexCount = (uint)vb.ElementCount,
                        IndexBuffer = ib.GpuAddress + (ulong)sub.IndexStart * sizeof(uint),
                        IndexFormat = Format.R32_UInt, IndexCount = (uint)sub.IndexCount,
                        Transform3x4 = 0,
                    },
                });
            }
            if (geoms.Count == 0) {
                geoms.Add(new RaytracingGeometryDescription {
                    Type = RaytracingGeometryType.Triangles, Flags = RaytracingGeometryFlags.Opaque,
                    Triangles = new RaytracingGeometryTrianglesDescription {
                        VertexBuffer = vbAddr,
                        VertexFormat = Format.R32G32B32_Float, VertexCount = (uint)vb.ElementCount,
                        IndexBuffer = ib.GpuAddress, IndexFormat = Format.R32_UInt, IndexCount = (uint)ib.ElementCount,
                        Transform3x4 = 0,
                    },
                });
            }
            var inputs = new BuildRaytracingAccelerationStructureInputs {
                Type = RaytracingAccelerationStructureType.BottomLevel, Layout = ElementsLayout.Array,
                Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
                DescriptorsCount = (uint)geoms.Count, GeometryDescriptions = geoms.ToArray(),
            };
            var pre = device5.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
            ID3D12Resource result = AsBuffer(pre.ResultDataMaxSizeInBytes, ResourceStates.RaytracingAccelerationStructure);
            ID3D12Resource scratch = AsBuffer(pre.ScratchDataSizeInBytes, ResourceStates.UnorderedAccess);
            blasByMesh[mesh] = result;
            toBuild.Add((mesh, inputs, result, scratch));
        }

        int instSize = Marshal.SizeOf<RaytracingInstanceDescription>();
        ID3D12Resource instBuf = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer((ulong)((long)instSize * instances.Count)), ResourceStates.GenericRead);
        byte* ip = instBuf.Map<byte>(0);
        for (int i = 0; i < instances.Count; i++) {
            var (mesh, world, _) = instances[i];
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
