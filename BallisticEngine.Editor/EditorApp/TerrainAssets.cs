namespace BallisticEngine.Editor;

internal static class TerrainAssets {
    const string DefaultFolder = "Assets/Default";
    const string CheckerTexture = "Assets/Default/Checker.bmp";
    const string CheckerMaterial = "Assets/Default/Checker.mat";

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

    static void WriteCheckerBmp(string absolutePath) {
        const int size = 512;
        const int cells = 16;
        const int cell = size / cells;

        byte[] a = [200, 200, 200];
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
