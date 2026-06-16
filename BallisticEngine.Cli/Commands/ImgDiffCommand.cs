using System.Globalization;

namespace BallisticEngine.Cli.Commands;

// `bal imgdiff <a.bmp> <b.bmp>` — perceptual image comparison for the engine's own screenshots.
// Per-pixel redmean color distance (a cheap perceptual weighting) after a 3x3 box prefilter that
// absorbs one-pixel antialiasing shimmer, pooled into the DUAL budgets golden-image practice
// needs: a global mean (overall drift) AND a windowed hotspot max (a small but broken region must
// not hide in the average — Unreal's MaximumLocalError precedent). Optional heatmap BMP shows
// WHERE the difference is — agents reason about the heatmap in one look instead of eyeballing
// two screenshots.
internal sealed class ImgDiffCommand : ICommand {
    public string Name => "imgdiff";
    public string Summary => "Perceptual diff of two BMP screenshots (+ heatmap).";
    public string Usage =>
        """
        Usage: bal imgdiff <a.bmp> <b.bmp> [--out heatmap.bmp] [--fail-mean X] [--fail-hotspot Y] [--no-blur]
          --out          write a heatmap BMP (black = same, red->yellow->white = different)
          --fail-mean    exit 1 when the mean error exceeds X     (range 0..1, try 0.002)
          --fail-hotspot exit 1 when the hotspot error exceeds Y  (range 0..1, try 0.02)
          --no-blur      skip the 3x3 antialiasing prefilter (exact per-pixel comparison)
        """;

    const int HotspotBlock = 32;     // hotspot = worst mean error over any 32x32 block
    const double VisibleThreshold = 0.05; // a pixel counts as "different" above this

    public int Run(string[] args) {
        string? pathA = null, pathB = null, outPath = null;
        double? failMean = null, failHotspot = null;
        bool blur = true;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outPath = Next(args, ref i, "--out"); break;
                case "--fail-mean": failMean = ParseDouble(Next(args, ref i, "--fail-mean"), "--fail-mean"); break;
                case "--fail-hotspot": failHotspot = ParseDouble(Next(args, ref i, "--fail-hotspot"), "--fail-hotspot"); break;
                case "--no-blur": blur = false; break;
                default:
                    if (pathA is null) pathA = args[i];
                    else if (pathB is null) pathB = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (pathA is null || pathB is null) throw new CliUsageException("expected two image paths");

        (int wa, int ha, byte[] a) = BmpReader.Read(pathA);
        (int wb, int hb, byte[] b) = BmpReader.Read(pathB);
        if (wa != wb || ha != hb)
            throw new Exception($"image sizes differ: {wa}x{ha} vs {wb}x{hb}");

        // Byte-identical shortcut (the deterministic-capture common case).
        if (a.AsSpan().SequenceEqual(b)) {
            Json.Write(new {
                identical = true, width = wa, height = ha,
                meanError = 0.0, maxError = 0.0, hotspotError = 0.0, differentPixels = 0, differentPct = 0.0,
            });
            return 0;
        }

        if (blur) {
            a = BoxBlur3(a, wa, ha);
            b = BoxBlur3(b, wa, ha);
        }

        // Per-pixel redmean distance, normalized to 0..1.
        var error = new double[wa * ha];
        double sum = 0, max = 0;
        int differentPixels = 0;
        for (int i = 0, p = 0; i < error.Length; i++, p += 3) {
            double bd = a[p] - b[p], gd = a[p + 1] - b[p + 1], rd = a[p + 2] - b[p + 2];
            double rMean = (a[p + 2] + b[p + 2]) * 0.5;
            double d = Math.Sqrt((2 + rMean / 256.0) * rd * rd + 4 * gd * gd + (2 + (255 - rMean) / 256.0) * bd * bd)
                       / 764.83; // max possible redmean distance -> 0..1
            error[i] = d;
            sum += d;
            if (d > max) max = d;
            if (d > VisibleThreshold) differentPixels++;
        }
        double mean = sum / error.Length;

        // Hotspot: worst block mean (catches a small broken region the global mean dilutes).
        double hotspot = 0;
        for (int by = 0; by < ha; by += HotspotBlock)
            for (int bx = 0; bx < wa; bx += HotspotBlock) {
                double blockSum = 0;
                int count = 0;
                for (int y = by; y < Math.Min(by + HotspotBlock, ha); y++)
                    for (int x = bx; x < Math.Min(bx + HotspotBlock, wa); x++) {
                        blockSum += error[y * wa + x];
                        count++;
                    }
                double blockMean = blockSum / count;
                if (blockMean > hotspot) hotspot = blockMean;
            }

        if (outPath is not null)
            WriteHeatmap(outPath, wa, ha, error);

        bool failed = (failMean is { } fm && mean > fm) || (failHotspot is { } fh && hotspot > fh);
        Json.Write(new {
            identical = false,
            width = wa, height = ha,
            meanError = Math.Round(mean, 6),
            maxError = Math.Round(max, 6),
            hotspotError = Math.Round(hotspot, 6),
            differentPixels,
            differentPct = Math.Round(differentPixels * 100.0 / error.Length, 3),
            heatmap = outPath,
            passed = !failed,
        });
        return failed ? 1 : 0;
    }

    // 3x3 box filter per channel — absorbs the single-pixel edge shimmer that plagues raw
    // pixel comparison without hiding real differences.
    static byte[] BoxBlur3(byte[] src, int width, int height) {
        var dst = new byte[src.Length];
        int rowBytes = width * 3;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++) {
                    int sum = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++) {
                        int sy = y + dy;
                        if (sy < 0 || sy >= height) continue;
                        for (int dx = -1; dx <= 1; dx++) {
                            int sx = x + dx;
                            if (sx < 0 || sx >= width) continue;
                            sum += src[sy * rowBytes + sx * 3 + c];
                            count++;
                        }
                    }
                    dst[y * rowBytes + x * 3 + c] = (byte)(sum / count);
                }
        return dst;
    }

    // Black -> red -> yellow -> white ramp; errors are amplified (x8, clamped) so subtle
    // differences are visible at a glance.
    static void WriteHeatmap(string path, int width, int height, double[] error) {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < error.Length; i++) {
            double t = Math.Clamp(error[i] * 8.0, 0, 1);
            byte r = (byte)(Math.Clamp(t * 3.0, 0, 1) * 255);
            byte g = (byte)(Math.Clamp(t * 3.0 - 1.0, 0, 1) * 255);
            byte bl = (byte)(Math.Clamp(t * 3.0 - 2.0, 0, 1) * 255);
            pixels[i * 3 + 0] = bl;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = r;
        }
        BmpWriter.Write(path, width, height, pixels);
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");

    static double ParseDouble(string s, string flag) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new CliUsageException($"{flag} expects a number (got '{s}')");
}
