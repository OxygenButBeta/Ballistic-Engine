using System.Security.Cryptography;
using System.Text;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

public static class Dx12ShaderCompiler {
    public static string CacheDirectory { get; set; }

    static readonly bool DiskCacheEnabled =
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADER_CACHE") != "0";

    public static byte[] Compile(DxcShaderStage stage, string source, string entryPoint,
        string fileName = "shader.hlsl") {
        string cachePath = CachePathFor(stage, source, entryPoint, fileName);
        if (cachePath is not null) {
            try {
                if (File.Exists(cachePath))
                    return File.ReadAllBytes(cachePath);
            }
            catch {
            }
        }

        var options = new DxcCompilerOptions {
            ShaderModel = DxcShaderModel.Model6_6,
        };
        using IDxcResult result = DxcCompiler.Compile(stage, source, entryPoint, options, fileName);
        if (result.GetStatus().Failure)
            throw new InvalidOperationException(
                $"HLSL compile failed ({stage} {entryPoint} in {fileName}):\n{result.GetErrors()}");
        byte[] dxil = result.GetObjectBytecodeArray();

        if (cachePath is not null) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                string tmp = cachePath + ".tmp";
                File.WriteAllBytes(tmp, dxil);
                File.Move(tmp, cachePath, overwrite: true);
            }
            catch {
            }
        }
        return dxil;
    }

    static string CachePathFor(DxcShaderStage stage, string source, string entryPoint, string fileName) {
        if (!DiskCacheEnabled) return null;
        string dir = CacheDirectory;
        if (string.IsNullOrEmpty(dir))
            return null;

        var sb = new StringBuilder(source.Length + 64);
        sb.Append((int)stage).Append('|').Append(entryPoint).Append('|').Append(fileName)
          .Append("|SM6_6|").Append(source);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Path.Combine(dir, Convert.ToHexString(hash) + ".dxil");
    }
}
