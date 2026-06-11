using System;
using BallisticEngine.UI;
using OpenTK.Graphics.OpenGL4;

namespace BallisticEngine;

// GL-side font: uploads a baked FontAtlas as a single-channel (R8) texture and lays out strings into
// positioned glyph quads for the text pass. Owns the GL texture; created once from a FontAtlas and
// reused across frames. Layout works in PANEL pixels at a requested font size, scaling the atlas's
// baked metrics by (fontSize / BakePixelHeight).
public sealed class GLUIFont : IDisposable
{
    public FontAtlas Atlas { get; }
    public int Texture { get; private set; }

    public GLUIFont(FontAtlas atlas)
    {
        Atlas = atlas;
        Upload();
    }

    void Upload()
    {
        Texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, Texture);

        // R8 single channel: the shader reads .r as the SDF distance. Linear filtering gives the SDF
        // its smooth interpolation between texels (essential for crisp AA at scale).
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8,
            Atlas.AtlasWidth, Atlas.AtlasHeight, 0, PixelFormat.Red, PixelType.UnsignedByte, Atlas.Pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // A positioned glyph: where to draw the quad (panel px) and which atlas region to sample.
    public struct PlacedGlyph
    {
        public float X, Y, W, H;       // quad in panel pixels
        public float U0, V0, U1, V1;   // atlas UVs (0..1)
    }

    // Measures the rendered width/height of `text` at `fontSize` (single line), including letter
    // spacing between glyphs. Height is the line height. Used to align the run within its element box.
    public (float width, float height) Measure(string text, float fontSize, float letterSpacing = 0f)
    {
        if (string.IsNullOrEmpty(text)) return (0f, LineHeight(fontSize));
        float scale = fontSize / Atlas.BakePixelHeight;
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
            if (Atlas.TryGetGlyph(text[i], out var g))
                w += g.Advance * scale + (i < text.Length - 1 ? letterSpacing : 0f);
        return (w, LineHeight(fontSize));
    }

    public float LineHeight(float fontSize) => Atlas.LineHeight * (fontSize / Atlas.BakePixelHeight);
    public float Ascent(float fontSize) => Atlas.Ascent * (fontSize / Atlas.BakePixelHeight);

    // Lays out `text` as a single line, aligned within `box` per `align`, returning each glyph's quad.
    // originX/originY anchor is computed from the alignment; the pen advances per glyph. SDF padding is
    // already baked into each glyph's box so quads slightly overlap the advance — that's correct.
    public void Layout(string text, float fontSize, Rect box, TextAlign align, float letterSpacing,
        Action<PlacedGlyph> emit)
    {
        if (string.IsNullOrEmpty(text)) return;
        float scale = fontSize / Atlas.BakePixelHeight;

        (float w, float h) = Measure(text, fontSize, letterSpacing);
        float ascent = Ascent(fontSize);

        // Horizontal anchor within the box.
        float penX = box.X + HorizontalOffset(align, box.Width, w);
        // Vertical: place the BASELINE. Top/middle/bottom alignment shifts the line block.
        float lineTop = box.Y + VerticalOffset(align, box.Height, h);
        float baseline = lineTop + ascent;

        for (int i = 0; i < text.Length; i++)
        {
            if (!Atlas.TryGetGlyph(text[i], out var g)) continue;
            if (g.Width > 0 && g.Height > 0)
            {
                float gx = penX + g.OffsetX * scale;
                float gy = baseline + g.OffsetY * scale; // OffsetY is negative (above baseline)
                emit(new PlacedGlyph
                {
                    X = gx, Y = gy, W = g.Width * scale, H = g.Height * scale,
                    U0 = g.AtlasX0 / (float)Atlas.AtlasWidth,
                    V0 = g.AtlasY0 / (float)Atlas.AtlasHeight,
                    U1 = g.AtlasX1 / (float)Atlas.AtlasWidth,
                    V1 = g.AtlasY1 / (float)Atlas.AtlasHeight,
                });
            }
            penX += g.Advance * scale + letterSpacing;
        }
    }

    static float HorizontalOffset(TextAlign a, float boxW, float textW) => a switch
    {
        TextAlign.UpperCenter or TextAlign.MiddleCenter or TextAlign.LowerCenter => (boxW - textW) * 0.5f,
        TextAlign.UpperRight or TextAlign.MiddleRight or TextAlign.LowerRight => boxW - textW,
        _ => 0f,
    };

    static float VerticalOffset(TextAlign a, float boxH, float lineH) => a switch
    {
        TextAlign.MiddleLeft or TextAlign.MiddleCenter or TextAlign.MiddleRight => (boxH - lineH) * 0.5f,
        TextAlign.LowerLeft or TextAlign.LowerCenter or TextAlign.LowerRight => boxH - lineH,
        _ => 0f,
    };

    public void Dispose()
    {
        if (Texture != 0) { GL.DeleteTexture(Texture); Texture = 0; }
    }
}
