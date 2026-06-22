using System.IO.Compression;

namespace BallisticEngine.AssetPipeline;

public static class TerrainHeightCodec {
    public static string Encode(float[] heights) {
        if (heights is null || heights.Length == 0)
            return null;

        var bytes = new byte[heights.Length * sizeof(float)];
        Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(bytes);

        return Convert.ToBase64String(output.ToArray());
    }

    public static bool TryDecode(string blob, int expectedCount, out float[] heights) {
        heights = null;
        if (string.IsNullOrWhiteSpace(blob))
            return false;

        try {
            var compressed = Convert.FromBase64String(blob);
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            var bytes = new byte[expectedCount * sizeof(float)];
            deflate.ReadExactly(bytes);

            heights = new float[expectedCount];
            Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
            return true;
        }
        catch {
            heights = null;
            return false;
        }
    }
}
