using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// FAZ -1 — Render-graph v2. Intended-usage state a pass declares for each read/write.
//
// This is the GRAPH-LEVEL access intent, deliberately coarser than the full D3D12_RESOURCE_STATES
// bitfield: a pass says "I read this as a shader resource" / "I write this as a UAV", and the
// graph derives the actual transition barriers (Granite invalidate/flush model — see Dx12RgGraph).
// ToD3D() maps each intent to a single canonical D3D12 state.

public enum Dx12RgResourceState {
    Common,
    ShaderRead,          // SRV in PS+non-PS stages (AllShaderResource — safe for cross-stage reads)
    PixelShaderRead,
    NonPixelShaderRead,  // compute/vertex SRV
    UnorderedAccess,
    RenderTarget,
    DepthWrite,
    DepthRead,
    CopySrc,
    CopyDst,
    IndirectArg,
    VertexConstantBuffer,
    Present,
    RaytracingAccelerationStructure,
}

public static class Dx12RgResourceStateExtensions {
    public static ResourceStates ToD3D(this Dx12RgResourceState s) => s switch {
        Dx12RgResourceState.Common               => ResourceStates.Common,
        Dx12RgResourceState.ShaderRead           => ResourceStates.AllShaderResource,
        Dx12RgResourceState.PixelShaderRead      => ResourceStates.PixelShaderResource,
        Dx12RgResourceState.NonPixelShaderRead   => ResourceStates.NonPixelShaderResource,
        Dx12RgResourceState.UnorderedAccess      => ResourceStates.UnorderedAccess,
        Dx12RgResourceState.RenderTarget         => ResourceStates.RenderTarget,
        Dx12RgResourceState.DepthWrite           => ResourceStates.DepthWrite,
        Dx12RgResourceState.DepthRead            => ResourceStates.DepthRead,
        Dx12RgResourceState.CopySrc              => ResourceStates.CopySource,
        Dx12RgResourceState.CopyDst              => ResourceStates.CopyDest,
        Dx12RgResourceState.IndirectArg          => ResourceStates.IndirectArgument,
        Dx12RgResourceState.VertexConstantBuffer => ResourceStates.VertexAndConstantBuffer,
        Dx12RgResourceState.Present              => ResourceStates.Present,
        Dx12RgResourceState.RaytracingAccelerationStructure => ResourceStates.RaytracingAccelerationStructure,
        _ => ResourceStates.Common,
    };

    public static bool IsWrite(this Dx12RgResourceState s) => s switch {
        Dx12RgResourceState.UnorderedAccess => true,
        Dx12RgResourceState.RenderTarget    => true,
        Dx12RgResourceState.DepthWrite      => true,
        Dx12RgResourceState.CopyDst         => true,
        _ => false,
    };

    public static bool IsRenderTargetOrDepth(this Dx12RgResourceState s) =>
        s is Dx12RgResourceState.RenderTarget or Dx12RgResourceState.DepthWrite or Dx12RgResourceState.DepthRead;
}
