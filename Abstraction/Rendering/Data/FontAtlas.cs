namespace BallisticEngine;

public sealed class FontAtlas
{
    public struct Glyph
    {
        public char Codepoint;

        public int AtlasX0, AtlasY0, AtlasX1, AtlasY1;

        public float OffsetX, OffsetY;

        public float Width, Height;
        public float Advance;
    }

    public byte[] Pixels;
    public int AtlasWidth;
    public int AtlasHeight;

    public float BakePixelHeight;

    public float SdfPadding;

    public float Ascent;
    public float Descent;
    public float LineHeight;

    public Dictionary<char, Glyph> Glyphs = new();

    public bool TryGetGlyph(char c, out Glyph glyph) => Glyphs.TryGetValue(c, out glyph);

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
                    lineW = wordW;
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
