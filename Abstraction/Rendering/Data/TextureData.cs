namespace BallisticEngine;

// CPU-side decoded image. Carries no GPU state, so it can be produced off the GL thread.
public readonly struct TextureData {
    public readonly int Width;
    public readonly int Height;
    public readonly TextureFormat Format;
    public readonly byte[] Pixels;

    public TextureData(int width, int height, TextureFormat format, byte[] pixels) {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
    }

    public bool IsValid => Pixels is not null && Width > 0 && Height > 0;
}
