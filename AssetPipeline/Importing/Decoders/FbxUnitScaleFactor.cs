using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BallisticEngine.AssetPipeline;

public static class FbxUnitScaleFactor {
    static readonly byte[] BinaryMagic = "Kaydara FBX Binary"u8.ToArray();
    const string Key = "UnitScaleFactor";

    public static double? Read(string path) {
        try {
            byte[] bytes = ReadPrefix(path, 1 << 20);
            return IsBinary(bytes) ? ReadBinary(bytes) : ReadAscii(bytes);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Could not read FBX unit scale from '{path}': {exception.Message}");
            return null;
        }
    }

    public static int? ReadUpAxis(string path) {
        try {
            byte[] bytes = ReadPrefix(path, 1 << 20);
            return IsBinary(bytes) ? ReadBinaryInt(bytes, "UpAxis") : ReadAsciiInt(bytes, "UpAxis");
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Could not read FBX up axis from '{path}': {exception.Message}");
            return null;
        }
    }

    static int? ReadBinaryInt(byte[] bytes, string key) {
        int keyIndex = IndexOf(bytes, Encoding.ASCII.GetBytes(key));
        if (keyIndex < 0)
            return null;

        for (var i = keyIndex + key.Length; i < Math.Min(bytes.Length - 10, keyIndex + 200); i++) {
            if (bytes[i] != (byte)'S' || bytes[i + 1] != 0 || bytes[i + 2] != 0 || bytes[i + 3] != 0 || bytes[i + 4] != 0)
                continue;
            if (bytes[i + 5] == (byte)'I')
                return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 6, 4));
            return null;
        }
        return null;
    }

    static int? ReadAsciiInt(byte[] bytes, string key) {
        var text = Encoding.UTF8.GetString(bytes);
        var keyIndex = text.IndexOf('"' + key + '"', StringComparison.Ordinal);
        if (keyIndex < 0)
            return null;
        var lineEnd = text.IndexOf('\n', keyIndex);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[keyIndex..lineEnd];
        var lastComma = line.LastIndexOf(',');
        if (lastComma < 0)
            return null;
        return int.TryParse(line[(lastComma + 1)..].Trim(), out int v) ? v : null;
    }

    static byte[] ReadPrefix(string path, int maxBytes) {
        using FileStream fs = File.OpenRead(path);
        var len = (int)Math.Min(maxBytes, fs.Length);
        var buffer = new byte[len];
        var read = 0;
        while (read < len) {
            int n = fs.Read(buffer, read, len - read);
            if (n == 0) break;
            read += n;
        }
        return read == len ? buffer : buffer[..read];
    }

    static bool IsBinary(byte[] bytes) {
        if (bytes.Length < BinaryMagic.Length)
            return false;
        for (var i = 0; i < BinaryMagic.Length; i++)
            if (bytes[i] != BinaryMagic[i])
                return false;
        return true;
    }

    static double? ReadBinary(byte[] bytes) {
        int keyIndex = IndexOf(bytes, Encoding.ASCII.GetBytes(Key));
        if (keyIndex < 0)
            return null;

        for (var i = keyIndex + Key.Length; i < bytes.Length - 9; i++) {
            if (bytes[i] != (byte)'D')
                continue;
            double value = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i + 1, 8));
            if (value is > 0 and < 1_000_000 && !double.IsNaN(value))
                return value;
        }
        return null;
    }

    static double? ReadAscii(byte[] bytes) {
        var text = Encoding.UTF8.GetString(bytes);
        var keyIndex = text.IndexOf(Key, StringComparison.Ordinal);
        if (keyIndex < 0)
            return null;

        var lineEnd = text.IndexOf('\n', keyIndex);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[keyIndex..lineEnd];

        var lastComma = line.LastIndexOf(',');
        if (lastComma < 0)
            return null;
        var token = line[(lastComma + 1)..].Trim();

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    static int IndexOf(byte[] haystack, byte[] needle) {
        for (var i = 0; i <= haystack.Length - needle.Length; i++) {
            var match = true;
            for (var j = 0; j < needle.Length; j++) {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
