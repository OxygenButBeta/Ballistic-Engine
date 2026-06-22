namespace BallisticEngine;

public static class BmpReader {
    public static (int Width, int Height, byte[] BgrPixels) Read(string path) {
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.ReadByte() != 'B' || br.ReadByte() != 'M')
            throw new InvalidDataException($"'{path}' is not a BMP file");
        br.ReadInt32();
        br.ReadInt32();
        int dataOffset = br.ReadInt32();
        int headerSize = br.ReadInt32();
        if (headerSize < 40)
            throw new InvalidDataException($"'{path}': unsupported BMP header (size {headerSize})");
        int width = br.ReadInt32();
        int rawHeight = br.ReadInt32();
        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        br.ReadInt16();
        int bpp = br.ReadInt16();
        int compression = br.ReadInt32();
        if (bpp != 24 || compression != 0)
            throw new InvalidDataException($"'{path}': only uncompressed 24bpp BMPs are supported (got {bpp}bpp, compression {compression})");

        br.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
        int rowBytes = width * 3;
        int padded = (rowBytes + 3) & ~3;
        var pixels = new byte[rowBytes * height];
        var row = new byte[padded];
        for (int y = 0; y < height; y++) {
            if (br.Read(row, 0, padded) != padded)
                throw new InvalidDataException($"'{path}': truncated pixel data");
            int destRow = topDown ? height - 1 - y : y;
            Array.Copy(row, 0, pixels, destRow * rowBytes, rowBytes);
        }
        return (width, height, pixels);
    }
}
