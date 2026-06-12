using BallisticEngine;

namespace BallisticEngine.Editor;

// Helpers for terrain authoring assets. Generates a shared checker material under Assets/Default the
// first time a terrain is created, so new terrains read with a clear tiling grid out of the box.
internal static class TerrainAssets {
    const string DefaultFolder = "Assets/Default";
    const string CheckerTexture = "Assets/Default/Checker.bmp";
    const string CheckerMaterial = "Assets/Default/Checker.mat";

    // Ensures the checker texture + material exist (creating them once), then returns the material's
    // project-relative path. Returns null if the project isn't available.
    public static string EnsureCheckerMaterial() {
        if (AssetDatabase.Project is null)
            return null;

        string dir = AssetDatabase.Project.ResolveAbsolute(DefaultFolder);
        Directory.CreateDirectory(dir);

        string texAbs = AssetDatabase.Project.ResolveAbsolute(CheckerTexture);
        if (!File.Exists(texAbs))
            WriteCheckerBmp(texAbs);

        string matAbs = AssetDatabase.Project.ResolveAbsolute(CheckerMaterial);
        if (!File.Exists(matAbs)) {
            // The checker is the Diffuse; the texture's own repetition is the grid (Material carries no
            // UV-tiling field, so the pattern frequency lives in the texture itself).
            File.WriteAllText(matAbs,
                "{\n" +
                "  \"version\": 1,\n" +
                "  \"shader\": \"Assets/Default/Shaders/Standard.shader\",\n" +
                "  \"textures\": {\n" +
                $"    \"Diffuse\": \"{CheckerTexture}\"\n" +
                "  },\n" +
                "  \"roughness\": 0.9,\n" +
                "  \"metallic\": 0.0\n" +
                "}\n");
        }

        return CheckerMaterial;
    }

    // Writes a 512x512 two-tone checker BMP (BGR, bottom-up, as BmpWriter expects). 16x16 cells so the
    // grid reads finely across a terrain whose UVs span 0..1.
    static void WriteCheckerBmp(string absolutePath) {
        const int size = 512;
        const int cells = 16;
        const int cell = size / cells;

        // Two soft greys — light enough to see lighting, distinct enough to read the grid.
        byte[] a = [200, 200, 200]; // B,G,R
        byte[] b = [120, 120, 120];

        var px = new byte[size * size * 3];
        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                bool dark = ((x / cell) + (y / cell)) % 2 == 0;
                byte[] c = dark ? b : a;
                int i = (y * size + x) * 3;
                px[i] = c[0]; px[i + 1] = c[1]; px[i + 2] = c[2];
            }
        }
        BmpWriter.Write(absolutePath, size, size, px);
    }
}
