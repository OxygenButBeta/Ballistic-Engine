using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

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
    readonly ID3D12Resource cbuffer;
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

        int cbSize = (Marshal.SizeOf<Constants>() + 255) & ~255;
        cbuffer = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);

        var rootParam = new RootParameter1(
            RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var rsDesc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout, new[] { rootParam });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(rsDesc));

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
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void Render(Dx12OffscreenTarget target, float yawRadians) {
        float aspect = (float)target.Width / target.Height;
        Matrix4x4 model = Matrix4x4.CreateRotationY(yawRadians) * Matrix4x4.CreateRotationX(0.5f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(2.5f, 2.5f, 6f), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            45f * (MathF.PI / 180f), aspect, 0.1f, 100f);

        var c = new Constants {
            Mvp = Matrix4x4.Transpose(model * view * proj),
            Model = Matrix4x4.Transpose(model),
            LightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f)),
            LightColor = new Vector3(1.0f, 0.97f, 0.9f),
            Ambient = new Vector3(0.15f, 0.16f, 0.2f),
        };
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
        Vector3[] faceN = {
            new(0,0,1), new(0,0,-1), new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0)
        };
        Vector3[] faceC = {
            new(0.9f,0.3f,0.3f), new(0.3f,0.9f,0.3f), new(0.3f,0.3f,0.9f),
            new(0.9f,0.9f,0.3f), new(0.9f,0.3f,0.9f), new(0.3f,0.9f,0.9f)
        };
        Vector3[][] faceV = {
            new[]{ new Vector3(-1,-1, 1), new Vector3( 1,-1, 1), new Vector3( 1, 1, 1), new Vector3(-1, 1, 1) }, new[]{ new Vector3( 1,-1,-1), new Vector3(-1,-1,-1), new Vector3(-1, 1,-1), new Vector3( 1, 1,-1) }, new[]{ new Vector3( 1,-1, 1), new Vector3( 1,-1,-1), new Vector3( 1, 1,-1), new Vector3( 1, 1, 1) }, new[]{ new Vector3(-1,-1,-1), new Vector3(-1,-1, 1), new Vector3(-1, 1, 1), new Vector3(-1, 1,-1) }, new[]{ new Vector3(-1, 1, 1), new Vector3( 1, 1, 1), new Vector3( 1, 1,-1), new Vector3(-1, 1,-1) }, new[]{ new Vector3(-1,-1,-1), new Vector3( 1,-1,-1), new Vector3( 1,-1, 1), new Vector3(-1,-1, 1) },
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
