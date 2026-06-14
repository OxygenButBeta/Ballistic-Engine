using System;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// Compiles HLSL to DXIL bytecode in-process via Vortice.Dxc (no external fxc/dxc.exe). The HLSL → DXIL
// equivalent of the GL backend's GLSL compile path. Used for raster shaders now; the same compiler does
// DXR lib_6_x in the later phases.
public static class Dx12ShaderCompiler {
    // Compile one HLSL entry point at the given stage (Vertex/Pixel/Compute/...) to DXIL bytecode.
    // Throws with the compiler error log on failure (fail loud — a silent bad shader is worse).
    public static byte[] Compile(DxcShaderStage stage, string source, string entryPoint,
        string fileName = "shader.hlsl") {
        var options = new DxcCompilerOptions {
            ShaderModel = DxcShaderModel.Model6_6, // SM6.6: bindless ResourceDescriptorHeap in Phase 4
        };
        using IDxcResult result = DxcCompiler.Compile(stage, source, entryPoint, options, fileName);
        if (result.GetStatus().Failure)
            throw new InvalidOperationException(
                $"HLSL compile failed ({stage} {entryPoint} in {fileName}):\n{result.GetErrors()}");
        return result.GetObjectBytecodeArray();
    }
}
