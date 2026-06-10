using System.Runtime.InteropServices;
using BallisticEngine.AssetPipeline;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine.Editor;

// Lazy thumbnail loader for the asset browser. Image assets get a 64x64 GL texture built
// from their Library artifact — at most one per frame so the UI never hitches. HDR sources
// are tone-mapped (Reinhard) for display.
internal sealed class ThumbnailCache {
    const int Size = 64;

    readonly Dictionary<Guid, int> ready = new();
    readonly Queue<Guid> pending = new();
    readonly HashSet<Guid> queued = new();

    // Returns the GL texture id, or 0 while the thumbnail is still loading.
    public int Get(Guid guid) {
        if (ready.TryGetValue(guid, out var texture))
            return texture;

        if (queued.Add(guid))
            pending.Enqueue(guid);
        return 0;
    }

    // Call once per frame.
    public void Pump() {
        if (pending.Count == 0)
            return;

        Guid guid = pending.Dequeue();
        try {
            ready[guid] = Build(guid);
        }
        catch {
            ready[guid] = 0; // unreadable: never retry, tile falls back to the colored box
        }
    }

    public void InvalidateAll() {
        foreach (var texture in ready.Values.Where(t => t != 0))
            GL.DeleteTexture(texture);
        ready.Clear();
        queued.Clear();
        pending.Clear();
    }

    static int Build(Guid guid) {
        if (!AssetDatabase.TryGetArtifactPath(guid, out var artifactPath))
            return 0;

        TextureData data = TextureArtifact.Read(artifactPath);
        var pixels = Downscale(in data);

        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Size, Size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    static byte[] Downscale(in TextureData data) {
        var output = new byte[Size * Size * 4];
        var isFloat = data.Format == TextureFormat.RGBA32F;
        ReadOnlySpan<float> floats = isFloat ? MemoryMarshal.Cast<byte, float>(data.Pixels) : default;

        for (var y = 0; y < Size; y++) {
            var srcY = Math.Min(data.Height - 1, y * data.Height / Size);
            for (var x = 0; x < Size; x++) {
                var srcX = Math.Min(data.Width - 1, x * data.Width / Size);
                var src = (srcY * data.Width + srcX) * 4;
                var dst = (y * Size + x) * 4;

                if (isFloat) {
                    for (var c = 0; c < 3; c++) {
                        var v = floats[src + c];
                        v = v / (1f + v); // Reinhard for HDR preview
                        output[dst + c] = (byte)Math.Clamp(MathF.Pow(v, 1f / 2.2f) * 255f, 0f, 255f);
                    }
                    output[dst + 3] = 255;
                }
                else {
                    output[dst] = data.Pixels[src];
                    output[dst + 1] = data.Pixels[src + 1];
                    output[dst + 2] = data.Pixels[src + 2];
                    output[dst + 3] = 255;
                }
            }
        }

        return output;
    }
}
