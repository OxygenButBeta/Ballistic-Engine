using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// MATH: the DX12 backend uses System.Numerics (SIMD-accelerated AND DX-convention — its
// CreatePerspectiveFieldOfView gives NDC z in [0,1] as DX12 expects, vs OpenGL's [-1,1]). The engine
// core stays on OpenTK.Mathematics; mesh/transform data is converted at the boundary when it arrives.

// Phase 2 smoke: a real vertex-buffer cube, lit by directional N·L, through an MVP constant buffer with
// depth testing. Proves mesh upload + CBV + depth + the minimal lit path on DX12 — the foundation the
// real mesh/material renderer (DX12HDRenderer) is built from. Self-contained + disposable.
public sealed class Dx12LitCubeTest : IDisposable {
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex {
        public Vector3 Pos;
        public Vector3 Normal;
        public Vector3 Color;
        public Vertex(Vector3 p, Vector3 n, Vector3 c) { Pos = p; Normal = n; Color = c; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 LightDir; public float Pad0;
        public Vector3 LightColor; public float Pad1;
        public Vector3 Ambient; public float Pad2;
    }

    readonly Dx12Device dev;
    readonly ID3D12RootSignature rootSig;
    readonly ID3D12PipelineState pso;
    readonly ID3D12Resource vbuffer;
    readonly ID3D12Resource ibuffer;
    readonly ID3D12Resource cbuffer;     // upload heap, persistently mapped
    readonly VertexBufferView vbv;
    readonly IndexBufferView ibv;
    readonly int indexCount;

    public Dx12LitCubeTest(Dx12Device device) {
        dev = device;
        (Vertex[] verts, ushort[] indices) = BuildCube();
        indexCount = indices.Length;

        vbuffer = CreateUploadBuffer(verts, out int vbBytes);
        vbv = new VertexBufferView(vbuffer.GPUVirtualAddress, (uint)vbBytes, (uint)Marshal.SizeOf<Vertex>());
        ibuffer = CreateUploadBuffer(indices, out int ibBytes);
        ibv = new IndexBufferView(ibuffer.GPUVirtualAddress, (uint)ibBytes, Format.R16_UInt);

        // Constant buffer: 256-aligned upload heap, persistently mapped (write per frame).
        int cbSize = (Marshal.SizeOf<Constants>() + 255) & ~255;
        cbuffer = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);

        // Root signature: one root CBV at b0.
        var rootParam = new RootParameter1(
            RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var rsDesc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout, new[] { rootParam });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(rsDesc));

        // Shaders + PSO with depth + input layout.
        string hlsl = EmbeddedShaderSource.ReadHlsl("LitCube.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "LitCube.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "LitCube.hlsl");

        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 24, 0));

        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise, // back-face cull (cube wound CCW-from-outside)
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,           // depth test + write, less
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    // Render the cube with a given model rotation into the target. Camera is a fixed look-at.
    public void Render(Dx12OffscreenTarget target, float yawRadians) {
        float aspect = (float)target.Width / target.Height;
        Matrix4x4 model = Matrix4x4.CreateRotationY(yawRadians) * Matrix4x4.CreateRotationX(0.5f);
        // System.Numerics LookAt/Perspective are RIGHT-HANDED with DX depth [0,1] — the convention DX12
        // wants. (This is what fixes the off-screen cube vs the OpenGL [-1,1] projection.)
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(2.5f, 2.5f, 6f), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            45f * (MathF.PI / 180f), aspect, 0.1f, 100f);

        // HLSL constant buffers read float4x4 COLUMN-major by default, but System.Numerics is row-major
        // in memory — so transpose on upload, then mul(float4(pos,1), MVP) in HLSL matches the CPU math.
        var c = new Constants {
            Mvp = Matrix4x4.Transpose(model * view * proj),
            Model = Matrix4x4.Transpose(model),
            LightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f)),
            LightColor = new Vector3(1.0f, 0.97f, 0.9f),
            Ambient = new Vector3(0.15f, 0.16f, 0.2f),
        };
        // Write the constant buffer via the Span map overload (no raw pointers).
        Span<Constants> cb = cbuffer.Map<Constants>(0, 1);
        cb[0] = c;
        cbuffer.Unmap(0);

        target.RenderInto(cl => {
            cl.SetGraphicsRootSignature(rootSig);
            cl.SetPipelineState(pso);
            cl.SetGraphicsRootConstantBufferView(0, cbuffer.GPUVirtualAddress);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.IASetVertexBuffers(0, vbv);
            cl.IASetIndexBuffer(ibv);
            cl.DrawIndexedInstanced((uint)indexCount, 1, 0, 0, 0);
        });
    }

    // An upload-heap buffer seeded with `data` (fine for a static smoke test; the real renderer uses
    // a DEFAULT-heap buffer with an upload copy).
    ID3D12Resource CreateUploadBuffer<T>(T[] data, out int byteSize) where T : unmanaged {
        byteSize = data.Length * Marshal.SizeOf<T>();
        ID3D12Resource buf = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteSize), ResourceStates.GenericRead);
        Span<T> dst = buf.Map<T>(0, data.Length);
        data.AsSpan().CopyTo(dst);
        buf.Unmap(0);
        return buf;
    }

    static (Vertex[], ushort[]) BuildCube() {
        // 24 verts (per-face normals + a distinct face color), 36 indices.
        Vector3[] faceN = {
            new(0,0,1), new(0,0,-1), new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0)
        };
        Vector3[] faceC = {
            new(0.9f,0.3f,0.3f), new(0.3f,0.9f,0.3f), new(0.3f,0.3f,0.9f),
            new(0.9f,0.9f,0.3f), new(0.9f,0.3f,0.9f), new(0.3f,0.9f,0.9f)
        };
        // Face corner offsets (CCW when viewed from outside along +normal).
        Vector3[][] faceV = {
            new[]{ new Vector3(-1,-1, 1), new Vector3( 1,-1, 1), new Vector3( 1, 1, 1), new Vector3(-1, 1, 1) }, // +Z
            new[]{ new Vector3( 1,-1,-1), new Vector3(-1,-1,-1), new Vector3(-1, 1,-1), new Vector3( 1, 1,-1) }, // -Z
            new[]{ new Vector3( 1,-1, 1), new Vector3( 1,-1,-1), new Vector3( 1, 1,-1), new Vector3( 1, 1, 1) }, // +X
            new[]{ new Vector3(-1,-1,-1), new Vector3(-1,-1, 1), new Vector3(-1, 1, 1), new Vector3(-1, 1,-1) }, // -X
            new[]{ new Vector3(-1, 1, 1), new Vector3( 1, 1, 1), new Vector3( 1, 1,-1), new Vector3(-1, 1,-1) }, // +Y
            new[]{ new Vector3(-1,-1,-1), new Vector3( 1,-1,-1), new Vector3( 1,-1, 1), new Vector3(-1,-1, 1) }, // -Y
        };
        var verts = new Vertex[24];
        var indices = new ushort[36];
        for (int f = 0; f < 6; f++) {
            for (int v = 0; v < 4; v++)
                verts[f * 4 + v] = new Vertex(faceV[f][v], faceN[f], faceC[f]);
            int b = f * 4;
            int ii = f * 6;
            indices[ii + 0] = (ushort)(b + 0); indices[ii + 1] = (ushort)(b + 1); indices[ii + 2] = (ushort)(b + 2);
            indices[ii + 3] = (ushort)(b + 0); indices[ii + 4] = (ushort)(b + 2); indices[ii + 5] = (ushort)(b + 3);
        }
        return (verts, indices);
    }

    public void Dispose() {
        cbuffer.Dispose();
        ibuffer.Dispose();
        vbuffer.Dispose();
        pso.Dispose();
        rootSig.Dispose();
    }
}
