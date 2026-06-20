using System.Collections.Generic;

namespace BallisticEngine;

// CPU-side baked font: a single-channel SDF atlas plus per-glyph metrics. Lives in Abstraction/ with
// the other CPU render-data types (MeshData, TextureData) — no GL, no font library here. The asset
// pipeline's FontBaker produces it from a .ttf; the GL backend uploads Pixels as an R8 texture and
// positions glyph quads from the metrics. Distances are stored as SDF so text stays crisp at any
// font size / panel scale (the shader thresholds the field).
public sealed class FontAtlas
{
    // One glyph's placement in the atlas and its layout metrics, all in PIXELS at the atlas's baked
    // pixel-height. The renderer scales these by (requestedFontSize / BakePixelHeight).
    public struct Glyph
    {
        public char Codepoint;
        // Atlas sub-rect in texel coordinates (x0,y0 = top-left, x1,y1 = bottom-right).
        public int AtlasX0, AtlasY0, AtlasX1, AtlasY1;
        // Offset from the pen origin (baseline, current x) to the glyph quad's top-left, in pixels.
        public float OffsetX, OffsetY;
        // Glyph quad size in pixels (matches the atlas sub-rect minus SDF padding handling).
        public float Width, Height;
        // How far to advance the pen after drawing this glyph, in pixels.
        public float Advance;
    }

    // R8 coverage/SDF data, row-major, AtlasWidth*AtlasHeight bytes (255 = inside, 0 = outside, the
    // edge crosses ~128). One channel; the shader reads .r.
    public byte[] Pixels;
    public int AtlasWidth;
    public int AtlasHeight;

    // The pixel height the glyphs were rasterized at. Requested font sizes scale relative to this.
    public float BakePixelHeight;

    // The SDF spread (padding) in pixels baked around each glyph edge — the shader's smoothstep width
    // derives from this so antialiasing matches the bake.
    public float SdfPadding;

    // Vertical metrics in pixels at BakePixelHeight: distance above/below the baseline + recommended
    // line advance. Used to position the baseline and lay out multi-line text.
    public float Ascent;
    public float Descent;   // typically negative (below baseline)
    public float LineHeight;

    // codepoint -> glyph, for O(1) lookup during layout. Missing glyphs fall back to the replacement
    // (or are skipped).
    public Dictionary<char, Glyph> Glyphs = new();

    public bool TryGetGlyph(char c, out Glyph glyph) => Glyphs.TryGetValue(c, out glyph);

    // CPU text measurement (no GL) so the layout engine can size a Label around its glyphs. Returns
    // the advance width + line height at `fontSize`, including letter spacing between glyphs. Honors '\n'
    // (multi-line height; width = widest line).
    public (float width, float height) Measure(string text, float fontSize, float letterSpacing = 0f)
    {
        float scale = BakePixelHeight > 0 ? fontSize / BakePixelHeight : 1f;
        float lineH = LineHeight * scale;
        if (string.IsNullOrEmpty(text)) return (0f, lineH);

        float maxW = 0f; int lines = 1; float cur = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') { if (cur > maxW) maxW = cur; cur = 0f; lines++; continue; }
            if (Glyphs.TryGetValue(c, out var g)) cur += g.Advance * scale + letterSpacing;
        }
        if (cur > maxW) maxW = cur;
        return (maxW, lines * lineH);
    }

    // Word-wrapping measurement (P4.3): lays the text into lines no wider than maxWidth, breaking on
    // spaces (and hard '\n'). Returns the used (width, height). A single word longer than maxWidth
    // overflows its line (CSS overflow-wrap:normal default). maxWidth <= 0 => no wrap (single-line per
    // paragraph), same as Measure.
    public (float width, float height) MeasureWrapped(string text, float fontSize, float letterSpacing, float maxWidth)
    {
        if (maxWidth <= 0f || string.IsNullOrEmpty(text)) return Measure(text, fontSize, letterSpacing);
        float scale = BakePixelHeight > 0 ? fontSize / BakePixelHeight : 1f;
        float lineH = LineHeight * scale;
        float spaceAdv = (Glyphs.TryGetValue(' ', out var sp) ? sp.Advance * scale : fontSize * 0.3f) + letterSpacing;

        float maxUsed = 0f; int lines = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            float lineW = 0f; bool any = false;
            foreach (var word in paragraph.Split(' '))
            {
                float wordW = WordWidth(word, scale, letterSpacing);
                float withSpace = any ? lineW + spaceAdv + wordW : wordW;
                if (any && withSpace > maxWidth)
                {
                    if (lineW > maxUsed) maxUsed = lineW;
                    lines++;
                    lineW = wordW; // word starts a new line
                }
                else lineW = withSpace;
                any = true;
            }
            if (lineW > maxUsed) maxUsed = lineW;
            lines++;
        }
        return (System.Math.Min(maxUsed, maxWidth), lines * lineH);
    }

    float WordWidth(string word, float scale, float letterSpacing)
    {
        float w = 0f;
        for (int i = 0; i < word.Length; i++)
            if (Glyphs.TryGetValue(word[i], out var g)) w += g.Advance * scale + letterSpacing;
        return w;
    }
}
