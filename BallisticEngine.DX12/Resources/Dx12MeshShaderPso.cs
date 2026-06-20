using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// R4 — builds a MESH-SHADER pipeline state object (AS + MS + PS) on Vortice 3.8.3, which has no high-level mesh-
// shader PSO helper. We hand-pack the D3D12_PIPELINE_STATE_STREAM_DESC blob: a sequence of 8-byte-aligned
// subobjects, each = { D3D12_PIPELINE_STATE_SUBOBJECT_TYPE (UINT, but the whole subobject is void*-aligned so the
// VALUE starts at an 8-byte boundary after the 4-byte type + 4 pad); value }, then CreatePipelineState with that
// blob. The native struct layouts (ShaderBytecode = {void*, SIZE_T}, RT_FORMAT_ARRAY = {DXGI_FORMAT[8], UINT})
// are reproduced exactly; a misalignment surfaces as a GBV/DRED error (caught CPU-side) — see the R2 precedent.
internal static class Dx12MeshShaderPso {
    // Subobject type ints (verified against Vortice's PipelineStateSubObjectType).
    const uint T_RootSignature = 0, T_PixelShader = 2, T_Blend = 8, T_SampleMask = 9, T_Rasterizer = 10,
               T_DepthStencil = 11, T_PrimitiveTopology = 14, T_RenderTargetFormats = 15, T_DepthStencilFormat = 16,
               T_SampleDescription = 17, T_AmplificationShader = 24, T_MeshShader = 25;

    [StructLayout(LayoutKind.Sequential)]
    struct ShaderBytecodeNative { public IntPtr pShaderBytecode; public nuint BytecodeLength; }
    [StructLayout(LayoutKind.Sequential)]
    struct SampleDescNative { public uint Count; public uint Quality; }
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct RtFormatArrayNative { public fixed uint RTFormats[8]; public uint NumRenderTargets; }

    // A tiny stream writer that keeps each subobject void*-aligned.
    sealed unsafe class Blob {
        readonly byte[] buf = new byte[2048];
        public int Len;
        void Align8() { while ((Len & 7) != 0) buf[Len++] = 0; }
        void AlignTo(int a) { while ((Len % a) != 0) buf[Len++] = 0; }
        void U32(uint v) { fixed (byte* p = &buf[Len]) *(uint*)p = v; Len += 4; }
        void Write<T>(in T v) where T : unmanaged { fixed (byte* p = &buf[Len]) { *(T*)p = v; } Len += sizeof(T); }
        // CD3DX12_PIPELINE_STATE_STREAM packing: each subobject is `struct alignas(void*) { SUBOBJECT_TYPE _Type;
        // ValueType _Inner; }`. So the subobject as a WHOLE is 8-aligned (Align8 before the type), then the 4-byte
        // type is written, then _Inner is padded to ITS OWN alignment (8 for pointer/struct values, 4 for uint).
        // Padding the value to a fixed 8 broke uint subobjects (DSVFormat/SampleMask/Topology) — those want value
        // at type+4, not type+8. Use the value's natural alignment.
        public void Sub<T>(uint type, in T value) where T : unmanaged { Align8(); U32(type); AlignTo(ValAlign<T>()); Write(value); }
        public void SubPtr(uint type, IntPtr ptr) { Align8(); U32(type); AlignTo(8); Write(ptr); }
        // Natural alignment of the value struct: 8 if it contains a pointer/8-byte field (ShaderBytecode, RtFormat
        // array is 4-aligned, SampleDesc 4-aligned), else 4. We special-case by size>=8 with a pointer → 8.
        static int ValAlign<T>() where T : unmanaged {
            var t = typeof(T);
            if (t == typeof(ShaderBytecodeNative)) return 8;   // {void*, SIZE_T}
            return 4;   // uint, SampleDescNative, RtFormatArrayNative, and the 4-byte-aligned D3D12 descs
        }
        public ReadOnlySpan<byte> Span => new(buf, 0, Len);
        public byte[] Buf => buf;
    }

    public static unsafe ID3D12PipelineState Create(
        ID3D12Device2 device, ID3D12RootSignature rootSig, byte[] amp, byte[] mesh, byte[] pixel,
        RasterizerDescription raster, BlendDescription blend, DepthStencilDescription depth,
        Format[] rtvFormats, Format dsvFormat) {

        // Pin the shader byte arrays for the lifetime of the create call (the blob holds raw pointers into them).
        fixed (byte* pAmp = amp) fixed (byte* pMesh = mesh) fixed (byte* pPix = pixel) {
            var b = new Blob();
            b.SubPtr(T_RootSignature, rootSig.NativePointer);
            b.Sub(T_AmplificationShader, new ShaderBytecodeNative { pShaderBytecode = (IntPtr)pAmp, BytecodeLength = (nuint)amp.Length });
            b.Sub(T_MeshShader, new ShaderBytecodeNative { pShaderBytecode = (IntPtr)pMesh, BytecodeLength = (nuint)mesh.Length });
            b.Sub(T_PixelShader, new ShaderBytecodeNative { pShaderBytecode = (IntPtr)pPix, BytecodeLength = (nuint)pixel.Length });
            b.Sub(T_Rasterizer, raster);
            b.Sub(T_Blend, blend);
            b.Sub(T_DepthStencil, depth);
            var rt = new RtFormatArrayNative { NumRenderTargets = (uint)rtvFormats.Length };
            for (int i = 0; i < rtvFormats.Length && i < 8; i++) rt.RTFormats[i] = (uint)rtvFormats[i];
            b.Sub(T_RenderTargetFormats, rt);
            b.Sub(T_DepthStencilFormat, (uint)dsvFormat);
            b.Sub(T_SampleDescription, new SampleDescNative { Count = 1, Quality = 0 });
            b.Sub(T_PrimitiveTopology, (uint)PrimitiveTopologyType.Triangle);
            b.Sub(T_SampleMask, uint.MaxValue);

            fixed (byte* pBlob = b.Buf) {
                var desc = new PipelineStateStreamDescription {
                    SizeInBytes = (nuint)b.Len,
                    SubObjectStream = (IntPtr)pBlob,
                };
                // EXPLICIT generic arg: CreatePipelineState has a CreatePipelineState<TData>(TData) overload that
                // `desc` would otherwise bind to (marshalling the DESCRIPTION struct as the subobject blob → E_
                // INVALIDARG). CreatePipelineState<ID3D12PipelineState>(PipelineStateStreamDescription) is the right
                // one (TData = the RETURN type, param = the stream desc).
                return device.CreatePipelineState<ID3D12PipelineState>(desc);
            }
        }
    }
}
