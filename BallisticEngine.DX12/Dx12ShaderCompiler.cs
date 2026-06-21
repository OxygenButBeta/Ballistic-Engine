using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

// Compiles HLSL to DXIL bytecode in-process via Vortice.Dxc (no external fxc/dxc.exe). The HLSL → DXIL
// equivalent of the GL backend's GLSL compile path. Used for raster shaders now; the same compiler does
// DXR lib_6_x in the later phases.
//
// On-disk DXIL cache: every editor/runtime launch used to re-run DXC for ~150 shaders (Lumen, GPU-driven,
// composite, post, sky, ...), which dominated boot time (seconds between "device ready" and "render graph
// compiled"). Now each compile is keyed by a hash of (stage+entry+file+SM+source) and the DXIL blob is
// persisted under Library\ShaderCache\. A launch with unchanged shaders is a pure file read (DXC never
// runs); editing any .hlsl changes its hash → automatic miss + recompile + re-cache. The cache dir is set
// once at bootstrap (CacheDirectory); when null (headless tools that never set a project) it degrades to
// always-compile, exactly the old behaviour.
public static class Dx12ShaderCompiler {
    // Set once at bootstrap to <project>\Library\ShaderCache. Null disables the on-disk cache.
    public static string CacheDirectory { get; set; }

    // BALLISTIC_DX12_SHADER_CACHE=0 bypasses the on-disk DXIL cache entirely (always recompile) — the escape hatch
    // for a suspect/corrupt cache, byte-identical to the old always-compile behaviour. Default ON. Read once.
    static readonly bool DiskCacheEnabled =
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADER_CACHE") != "0";

    // Compile one HLSL entry point at the given stage (Vertex/Pixel/Compute/...) to DXIL bytecode.
    // Throws with the compiler error log on failure (fail loud — a silent bad shader is worse).
    public static byte[] Compile(DxcShaderStage stage, string source, string entryPoint,
        string fileName = "shader.hlsl") {
        string cachePath = CachePathFor(stage, source, entryPoint, fileName);
        if (cachePath is not null) {
            try {
                if (File.Exists(cachePath))
                    return File.ReadAllBytes(cachePath);
            }
            catch { /* unreadable cache entry — fall through to recompile */ }
        }

        var options = new DxcCompilerOptions {
            ShaderModel = DxcShaderModel.Model6_6, // SM6.6: bindless ResourceDescriptorHeap in Phase 4
        };
        using IDxcResult result = DxcCompiler.Compile(stage, source, entryPoint, options, fileName);
        if (result.GetStatus().Failure)
            throw new InvalidOperationException(
                $"HLSL compile failed ({stage} {entryPoint} in {fileName}):\n{result.GetErrors()}");
        byte[] dxil = result.GetObjectBytecodeArray();

        if (cachePath is not null) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                // Write to a temp file then move, so a crash mid-write never leaves a truncated blob
                // that a later launch would load as a corrupt shader.
                string tmp = cachePath + ".tmp";
                File.WriteAllBytes(tmp, dxil);
                File.Move(tmp, cachePath, overwrite: true);
            }
            catch { /* cache write is best-effort; compiling still produced valid bytecode */ }
        }
        return dxil;
    }

    static string CachePathFor(DxcShaderStage stage, string source, string entryPoint, string fileName) {
        if (!DiskCacheEnabled)   // BALLISTIC_DX12_SHADER_CACHE=0 → no cache path → always compile
            return null;
        string dir = CacheDirectory;
        if (string.IsNullOrEmpty(dir))
            return null;

        // The key MUST include everything that changes the DXIL: stage, entry, the file name (DXC bakes
        // it into debug info / diagnostics), the shader model, and the full source text.
        var sb = new StringBuilder(source.Length + 64);
        sb.Append((int)stage).Append('|').Append(entryPoint).Append('|').Append(fileName)
          .Append("|SM6_6|").Append(source);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Path.Combine(dir, Convert.ToHexString(hash) + ".dxil");
    }
}
