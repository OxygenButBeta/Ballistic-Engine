using System.Globalization;
using System.Text;

namespace BallisticEngine;

// Entity-ID map captures — the renderer's "labeled screenshot": an on-demand pass renders every
// visible opaque submesh with a unique integer ID, and this class turns the readback into
//   <path>.json — per-entity / per-submesh screen-space bounding boxes, pixel counts, coverage
//   <path>.bmp  — the ID map color-coded for humans/VLMs (golden-ratio hues)
// This is occlusion-aware ground truth ("what is ACTUALLY visible where"), the machine-readable
// scene the perception layer promises agents: ask "is the Player on screen", "what occupies the
// top-left", "what entity is at pixel (x,y)" without guessing from pixels.
//
// Request flow mirrors Screenshots: queue here, the GL renderer drains due requests at the end of
// its frame (player/headless path). v1 limits: opaque geometry only, skinned renderers are skipped
// (counted in the report), alpha-cutout draws claim their full triangles.
public static class IdMaps {
    public sealed class Request {
        public required string Path;
        public int SettleFrames;
        public Action<string> OnSaved;
    }

    // One drawn submesh: identity filled by the renderer at draw time (ids are 1-based; the
    // legend index is id-1), pixel stats filled here from the readback.
    public sealed class Entry {
        public string Entity;
        public string EntityId;
        public string SubMesh;
        public int SubMeshIndex;

        internal int Pixels;
        internal int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
    }

    static readonly object gate = new();
    static readonly List<Request> pending = new();

    public static void Capture(string path, int settleFrames = 0, Action<string> onSaved = null) {
        if (string.IsNullOrWhiteSpace(path))
            return;
        lock (gate)
            pending.Add(new Request { Path = path, SettleFrames = settleFrames, OnSaved = onSaved });
    }

    public static int PendingCount {
        get { lock (gate) return pending.Count; }
    }

    // Called by the renderer once per presented frame (player path). Same countdown semantics as
    // Screenshots.DueThisFrame.
    public static List<Request> DueThisFrame() {
        lock (gate) {
            if (pending.Count == 0)
                return null;
            List<Request> due = null;
            for (int i = pending.Count - 1; i >= 0; i--) {
                if (pending[i].SettleFrames-- > 0)
                    continue;
                (due ??= new()).Add(pending[i]);
                pending.RemoveAt(i);
            }
            return due;
        }
    }

    // Aggregates the ID readback into the JSON report + color-coded BMP. `ids` is the raw
    // GL readback (bottom-up rows, 0 = background, 1-based ids indexing legend[id-1]).
    public static void WriteOutputs(string path, int width, int height,
        uint[] ids, List<Entry> legend, int skippedSkinnedRenderers) {
        // Pixel stats per entry. Image-space coords (top-left origin): flip GL's bottom-up rows.
        for (int row = 0; row < height; row++) {
            int imageY = height - 1 - row;
            int baseIndex = row * width;
            for (int x = 0; x < width; x++) {
                uint id = ids[baseIndex + x];
                if (id == 0 || id > (uint)legend.Count)
                    continue;
                Entry e = legend[(int)id - 1];
                e.Pixels++;
                if (x < e.MinX) e.MinX = x;
                if (x > e.MaxX) e.MaxX = x;
                if (imageY < e.MinY) e.MinY = imageY;
                if (imageY > e.MaxY) e.MaxY = imageY;
            }
        }

        // Group submesh entries by entity, merge boxes, drop fully-occluded entries.
        var byEntity = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var entityOrder = new List<string>();
        int occludedSubmeshes = 0;
        foreach (Entry e in legend) {
            if (e.Pixels == 0) { occludedSubmeshes++; continue; }
            string key = e.EntityId ?? e.Entity ?? "?";
            if (!byEntity.TryGetValue(key, out List<Entry> list)) {
                byEntity[key] = list = new();
                entityOrder.Add(key);
            }
            list.Add(e);
        }
        entityOrder.Sort((a, b) => byEntity[b].Sum(e => e.Pixels).CompareTo(byEntity[a].Sum(e => e.Pixels)));

        var sb = new StringBuilder(4096);
        sb.Append("{\n");
        sb.Append($"  \"width\": {width}, \"height\": {height},\n");
        sb.Append($"  \"skippedSkinnedRenderers\": {skippedSkinnedRenderers},\n");
        sb.Append($"  \"occludedSubmeshes\": {occludedSubmeshes},\n");
        sb.Append("  \"entities\": [\n");
        for (int i = 0; i < entityOrder.Count; i++) {
            List<Entry> parts = byEntity[entityOrder[i]];
            Entry first = parts[0];
            int pixels = parts.Sum(e => e.Pixels);
            int minX = parts.Min(e => e.MinX), minY = parts.Min(e => e.MinY);
            int maxX = parts.Max(e => e.MaxX), maxY = parts.Max(e => e.MaxY);
            double coverage = pixels / (double)(width * height);

            sb.Append("    {\n");
            sb.Append($"      \"name\": {JsonString(first.Entity)},\n");
            sb.Append($"      \"id\": {JsonString(first.EntityId)},\n");
            sb.Append($"      \"pixels\": {pixels},\n");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"      \"coverage\": {coverage:0.0000},\n"));
            sb.Append($"      \"bbox\": {Box(minX, minY, maxX, maxY)}");
            // Submesh detail only when it adds information (split scene meshes, multi-part entities).
            if (parts.Count > 1 || parts[0].SubMesh is not null) {
                sb.Append(",\n      \"submeshes\": [\n");
                parts.Sort((a, b) => b.Pixels.CompareTo(a.Pixels));
                for (int p = 0; p < parts.Count; p++) {
                    Entry e = parts[p];
                    sb.Append($"        {{ \"name\": {JsonString(e.SubMesh)}, \"index\": {e.SubMeshIndex}, " +
                              $"\"pixels\": {e.Pixels}, \"bbox\": {Box(e.MinX, e.MinY, e.MaxX, e.MaxY)} }}");
                    sb.Append(p < parts.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("      ]\n");
            }
            else {
                sb.Append('\n');
            }
            sb.Append(i < entityOrder.Count - 1 ? "    },\n" : "    }\n");
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(path + ".json", sb.ToString());

        // Color-coded visualization: distinct hue per id (golden-ratio walk), black background.
        // ReadPixels rows are already bottom-up, which is BMP's row order — write straight through.
        var pixels24 = new byte[width * height * 3];
        for (int i = 0; i < ids.Length; i++) {
            uint id = ids[i];
            if (id == 0)
                continue;
            (byte r, byte g, byte b) = ColorForId(id);
            pixels24[i * 3 + 0] = b;
            pixels24[i * 3 + 1] = g;
            pixels24[i * 3 + 2] = r;
        }
        BmpWriter.Write(path + ".bmp", width, height, pixels24);
    }

    static string Box(int minX, int minY, int maxX, int maxY) =>
        $"{{ \"x\": {minX}, \"y\": {minY}, \"w\": {maxX - minX + 1}, \"h\": {maxY - minY + 1} }}";

    static string JsonString(string s) {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s) {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    static (byte r, byte g, byte b) ColorForId(uint id) {
        double hue = id * 0.61803398875 % 1.0;
        double s = 0.65, v = 0.95;
        double h6 = hue * 6.0;
        int sector = (int)h6;
        double f = h6 - sector;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (double r, double g, double b) = sector switch {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
