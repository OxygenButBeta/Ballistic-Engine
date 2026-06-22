using static StbTrueTypeSharp.StbTrueType;

namespace BallisticEngine.AssetPipeline;

public static unsafe class FontBaker
{
    const int FirstChar = 32;
    const int LastChar = 126;
    const int Padding = 4;
    const byte OnEdgeValue = 128;
    const float PixelDistScale = 128f / Padding;

    public static FontAtlas Bake(string ttfPath, float pixelHeight = 48f)
    {
        if (!File.Exists(ttfPath))
        {
            Debugging.LogError($"FontBaker: file not found '{ttfPath}'.");
            return null;
        }

        try
        {
            return BakeFromBytes(File.ReadAllBytes(ttfPath), pixelHeight);
        }
        catch (Exception e)
        {
            Debugging.LogError($"FontBaker: failed to bake '{ttfPath}' — {e.Message}");
            return null;
        }
    }

    public static FontAtlas BakeFromBytes(byte[] ttf, float pixelHeight)
    {
        FontAtlas result = null;
        Exception error = null;
        var worker = new System.Threading.Thread(() =>
        {
            try { result = BakeCore(ttf, pixelHeight); }
            catch (Exception e) { error = e; }
        }, maxStackSize: 64 * 1024 * 1024);
        worker.Start();
        worker.Join();
        if (error != null)
            Debugging.LogError($"FontBaker: bake error — {error.Message}");
        return result;
    }

    static FontAtlas BakeCore(byte[] ttf, float pixelHeight)
    {
        var font = new stbtt_fontinfo();
        fixed (byte* pTtf = ttf)
        {
            if (stbtt_InitFont(font, pTtf, 0) == 0)
                throw new InvalidOperationException("stbtt_InitFont failed (not a valid TTF?).");

            float scale = stbtt_ScaleForPixelHeight(font, pixelHeight);

            int ascent, descent, lineGap;
            stbtt_GetFontVMetrics(font, &ascent, &descent, &lineGap);

            var atlas = new FontAtlas
            {
                BakePixelHeight = pixelHeight,
                SdfPadding = Padding,
                Ascent = ascent * scale,
                Descent = descent * scale,
                LineHeight = (ascent - descent + lineGap) * scale,
            };

            int count = LastChar - FirstChar + 1;
            var bitmaps = new byte[count][];
            var gw = new int[count];
            var gh = new int[count];
            var gxoff = new int[count];
            var gyoff = new int[count];

            for (int i = 0; i < count; i++)
            {
                int cp = FirstChar + i;
                int w, h, xoff, yoff;
                byte* sdf = stbtt_GetCodepointSDF(font, scale, cp, Padding, OnEdgeValue, PixelDistScale,
                    &w, &h, &xoff, &yoff);

                if (sdf != null && w > 0 && h > 0)
                {
                    var managed = new byte[w * h];
                    for (int k = 0; k < managed.Length; k++) managed[k] = sdf[k];
                    bitmaps[i] = managed;
                    gw[i] = w; gh[i] = h; gxoff[i] = xoff; gyoff[i] = yoff;
                    stbtt_FreeSDF(sdf, null);
                }
            }

            const int gutter = 1;
            int atlasW = ChooseAtlasWidth(gw, gh, count);
            PackAndBlit(atlas, bitmaps, gw, gh, atlasW, gutter, font, scale);

            for (int i = 0; i < count; i++)
            {
                int cp = FirstChar + i;
                int advance, lsb;
                stbtt_GetCodepointHMetrics(font, cp, &advance, &lsb);

                var glyph = atlas.TryGetGlyph((char)cp, out var existing) ? existing : new FontAtlas.Glyph();
                glyph.Codepoint = (char)cp;
                glyph.Advance = advance * scale;
                glyph.OffsetX = gxoff[i];
                glyph.OffsetY = gyoff[i];
                glyph.Width = gw[i];
                glyph.Height = gh[i];
                atlas.Glyphs[(char)cp] = glyph;
            }

            return atlas;
        }
    }

    static int ChooseAtlasWidth(int[] gw, int[] gh, int count)
    {
        long area = 0;
        for (int i = 0; i < count; i++) area += (gw[i] + 2L) * (gh[i] + 2L);
        int side = (int)Math.Ceiling(Math.Sqrt(area));
        int w = 64;
        while (w < side) w <<= 1;
        return Math.Max(128, w);
    }

    static void PackAndBlit(FontAtlas atlas, byte[][] bitmaps, int[] gw, int[] gh, int atlasW, int gutter,
        stbtt_fontinfo font, float scale)
    {
        int penX = gutter, penY = gutter, rowH = 0;
        int usedH = gutter;

        int count = bitmaps.Length;
        var px = new int[count];
        var py = new int[count];
        for (int i = 0; i < count; i++)
        {
            int w = gw[i], h = gh[i];
            if (w == 0 || h == 0) { px[i] = -1; continue; }
            if (penX + w + gutter > atlasW)
            {
                penX = gutter;
                penY += rowH + gutter;
                rowH = 0;
            }
            px[i] = penX; py[i] = penY;
            penX += w + gutter;
            rowH = Math.Max(rowH, h);
            usedH = Math.Max(usedH, penY + h + gutter);
        }

        int atlasH = 64;
        while (atlasH < usedH) atlasH <<= 1;

        atlas.AtlasWidth = atlasW;
        atlas.AtlasHeight = atlasH;
        atlas.Pixels = new byte[atlasW * atlasH];

        for (int i = 0; i < count; i++)
        {
            if (px[i] < 0) continue;
            int w = gw[i], h = gh[i];
            var bmp = bitmaps[i];
            for (int y = 0; y < h; y++)
            {
                int dstRow = (py[i] + y) * atlasW + px[i];
                int srcRow = y * w;
                for (int x = 0; x < w; x++)
                    atlas.Pixels[dstRow + x] = bmp[srcRow + x];
            }

            int cp = FontBaker.FirstChar + i;
            atlas.Glyphs[(char)cp] = new FontAtlas.Glyph
            {
                Codepoint = (char)cp,
                AtlasX0 = px[i], AtlasY0 = py[i],
                AtlasX1 = px[i] + w, AtlasY1 = py[i] + h,
            };
        }
    }
}
