namespace BallisticEngine;

public readonly struct TextureData {
    public readonly int Width;
    public readonly int Height;
    public readonly TextureFormat Format;
    public readonly byte[] Pixels;
    public readonly int MipCount;

    public TextureData(int width, int height, TextureFormat format, byte[] pixels, int mipCount = 1) {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
        MipCount = mipCount < 1 ? 1 : mipCount;
    }

    public bool IsValid => Pixels is not null && Width > 0 && Height > 0;
}
