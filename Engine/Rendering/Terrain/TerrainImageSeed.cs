namespace BallisticEngine;

public static class TerrainImageSeed {
    public static bool FromImage(TerrainAsset terrain, string imageAssetPath) {
        if (terrain is null || string.IsNullOrEmpty(imageAssetPath))
            return false;

        if (!AssetDatabase.TryLoadTextureData(imageAssetPath, out TextureData image) || !image.IsValid) {
            Debugging.LogWarning($"Terrain: heightmap image '{imageAssetPath}' could not be loaded; height field unchanged.");
            return false;
        }

        int res = terrain.Resolution;
        for (int z = 0; z < res; z++) {
            for (int x = 0; x < res; x++) {
                float u = res > 1 ? x / (float)(res - 1) : 0f;
                float v = res > 1 ? z / (float)(res - 1) : 0f;
                terrain.Heights[z * res + x] = Math.Clamp(SampleRed(in image, u, v), 0f, 1f);
            }
        }

        terrain.BumpRevision();
        return true;
    }

    static float SampleRed(in TextureData image, float u, float v) {
        float fx = u * (image.Width - 1);
        float fy = v * (image.Height - 1);
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        float tx = fx - x0, ty = fy - y0;

        float r00 = Red(in image, x0, y0);
        float r10 = Red(in image, x1, y0);
        float r01 = Red(in image, x0, y1);
        float r11 = Red(in image, x1, y1);

        float r0 = r00 + (r10 - r00) * tx;
        float r1 = r01 + (r11 - r01) * tx;
        return r0 + (r1 - r0) * ty;
    }

    static float Red(in TextureData image, int x, int y) {
        int pixel = y * image.Width + x;
        if (image.Format == TextureFormat.RGBA32F) {
            int byteOffset = pixel * 16;
            return BitConverter.ToSingle(image.Pixels, byteOffset);
        }

        return image.Pixels[pixel * 4] / 255f;
    }
}
