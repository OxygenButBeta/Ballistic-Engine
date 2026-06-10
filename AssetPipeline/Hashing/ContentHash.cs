using System.Security.Cryptography;
using System.Text;

namespace BallisticEngine.AssetPipeline;

public static class ContentHash {
    public static string HashFile(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashString(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
