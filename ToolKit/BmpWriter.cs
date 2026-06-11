namespace BallisticEngine;

// Minimal 24bpp BMP encoder. Rows are stored bottom-up in BGR byte order — exactly what
// GL.ReadPixels(..., Bgr, UnsignedByte) produces, so the engine's screenshot path writes the
// readback buffer verbatim with no flips or swizzles. BCL-only on purpose (ToolKit layer).
public static class BmpWriter {
    public static void Write(string path, int width, int height, byte[] bgrPixels) {
        var rowBytes = width * 3;
        var padded = (rowBytes + 3) & ~3;
        var dataSize = padded * height;

        using var bw = new BinaryWriter(File.Create(path));
        // BITMAPFILEHEADER
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(54 + dataSize);
        bw.Write(0);
        bw.Write(54);
        // BITMAPINFOHEADER
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0);
        bw.Write(dataSize);
        bw.Write(2835); // 72 dpi
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        var pad = new byte[padded - rowBytes];
        for (var y = 0; y < height; y++) {
            bw.Write(bgrPixels, y * rowBytes, rowBytes);
            if (pad.Length > 0)
                bw.Write(pad);
        }
    }
}
