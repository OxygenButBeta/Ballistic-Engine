
namespace BallisticEngine;

public static class TerrainSculpt {
    public enum Brush { Raise, Lower, Smooth, Flatten, Set }

    public static bool Apply(TerrainAsset terrain, Brush brush, Vector3 localCenter,
        float radiusWorld, float strength, float targetHeight = 0f) {
        if (terrain is null)
            return false;

        int res = terrain.Resolution;
        float halfX = terrain.Size.X * 0.5f;
        float halfZ = terrain.Size.Y * 0.5f;
        float stepX = terrain.Size.X / (res - 1);
        float stepZ = terrain.Size.Y / (res - 1);

        float cx = localCenter.X, cz = localCenter.Z;
        int minX = Math.Clamp((int)MathF.Floor((cx - radiusWorld + halfX) / stepX), 0, res - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling((cx + radiusWorld + halfX) / stepX), 0, res - 1);
        int minZ = Math.Clamp((int)MathF.Floor((cz - radiusWorld + halfZ) / stepZ), 0, res - 1);
        int maxZ = Math.Clamp((int)MathF.Ceiling((cz + radiusWorld + halfZ) / stepZ), 0, res - 1);
        if (minX > maxX || minZ > maxZ)
            return false;

        float scale = MathF.Max(terrain.HeightScale, 1e-4f);
        bool changed = false;

        for (int z = minZ; z <= maxZ; z++) {
            for (int x = minX; x <= maxX; x++) {
                float wx = -halfX + x * stepX;
                float wz = -halfZ + z * stepZ;
                float dist = MathF.Sqrt((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz));
                if (dist > radiusWorld)
                    continue;

                float falloff = Falloff(dist / radiusWorld);
                if (falloff <= 0f)
                    continue;

                int i = z * res + x;
                float h = terrain.Heights[i];
                float next = h;

                switch (brush) {
                    case Brush.Raise:
                        next = h + strength / scale * falloff;
                        break;
                    case Brush.Lower:
                        next = h - strength / scale * falloff;
                        break;
                    case Brush.Smooth:
                        next = MathHelper.Lerp(h, NeighborAverage(terrain.Heights, res, x, z), strength * falloff);
                        break;
                    case Brush.Flatten:
                        next = MathHelper.Lerp(h, targetHeight, strength * falloff);
                        break;
                    case Brush.Set:
                        next = MathHelper.Lerp(h, targetHeight, falloff);
                        break;
                }

                next = Math.Clamp(next, 0f, 1f);
                if (next != h) {
                    terrain.Heights[i] = next;
                    changed = true;
                }
            }
        }

        return changed;
    }

    static float Falloff(float t) {
        t = Math.Clamp(1f - t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static float NeighborAverage(float[] heights, int res, int x, int z) {
        float sum = 0f;
        int count = 0;
        for (int dz = -1; dz <= 1; dz++) {
            for (int dx = -1; dx <= 1; dx++) {
                int nx = Math.Clamp(x + dx, 0, res - 1);
                int nz = Math.Clamp(z + dz, 0, res - 1);
                sum += heights[nz * res + nx];
                count++;
            }
        }
        return sum / count;
    }

    public static bool Raycast(TerrainAsset terrain, Vector3 localOrigin, Vector3 localDir,
        out Vector3 localHit, float maxDistance = 100000f) {
        localHit = default;
        if (terrain is null)
            return false;

        localDir = localDir.Normalized();
        float halfX = terrain.Size.X * 0.5f;
        float halfZ = terrain.Size.Y * 0.5f;
        float cell = MathF.Min(terrain.Size.X, terrain.Size.Y) / (terrain.Resolution - 1);
        float step = MathF.Max(cell, 1e-3f);

        float prevDiff = SignedHeightDiff(terrain, localOrigin, halfX, halfZ, out _);
        float traveled = 0f;
        Vector3 prev = localOrigin;

        while (traveled < maxDistance) {
            traveled += step;
            Vector3 sample = localOrigin + localDir * traveled;

            if (OutOfBoundsAndLeaving(sample, localDir, halfX, halfZ))
                break;

            float diff = SignedHeightDiff(terrain, sample, halfX, halfZ, out bool inside);
            if (inside && prevDiff > 0f && diff <= 0f) {
                localHit = Bisect(terrain, prev, sample, halfX, halfZ);
                return true;
            }

            prevDiff = diff;
            prev = sample;
        }

        return false;
    }

    static float SignedHeightDiff(TerrainAsset terrain, Vector3 point, float halfX, float halfZ, out bool inside) {
        inside = point.X >= -halfX && point.X <= halfX && point.Z >= -halfZ && point.Z <= halfZ;
        return point.Y - SurfaceHeight(terrain, point.X, point.Z, halfX, halfZ);
    }

    static bool OutOfBoundsAndLeaving(Vector3 p, Vector3 dir, float halfX, float halfZ) {
        if (p.X < -halfX && dir.X <= 0f) return true;
        if (p.X > halfX && dir.X >= 0f) return true;
        if (p.Z < -halfZ && dir.Z <= 0f) return true;
        if (p.Z > halfZ && dir.Z >= 0f) return true;
        return false;
    }

    static Vector3 Bisect(TerrainAsset terrain, Vector3 above, Vector3 below, float halfX, float halfZ) {
        for (int i = 0; i < 12; i++) {
            Vector3 mid = (above + below) * 0.5f;
            float diff = mid.Y - SurfaceHeight(terrain, mid.X, mid.Z, halfX, halfZ);
            if (diff > 0f)
                above = mid;
            else
                below = mid;
        }
        return (above + below) * 0.5f;
    }

    public static float SurfaceHeight(TerrainAsset terrain, float localX, float localZ, float halfX, float halfZ) {
        int res = terrain.Resolution;
        float fx = (localX + halfX) / terrain.Size.X * (res - 1);
        float fz = (localZ + halfZ) / terrain.Size.Y * (res - 1);
        fx = Math.Clamp(fx, 0f, res - 1);
        fz = Math.Clamp(fz, 0f, res - 1);

        int x0 = (int)MathF.Floor(fx), z0 = (int)MathF.Floor(fz);
        int x1 = Math.Min(x0 + 1, res - 1), z1 = Math.Min(z0 + 1, res - 1);
        float tx = fx - x0, tz = fz - z0;

        float h00 = terrain.Heights[z0 * res + x0];
        float h10 = terrain.Heights[z0 * res + x1];
        float h01 = terrain.Heights[z1 * res + x0];
        float h11 = terrain.Heights[z1 * res + x1];

        float h0 = MathHelper.Lerp(h00, h10, tx);
        float h1 = MathHelper.Lerp(h01, h11, tx);
        return MathHelper.Lerp(h0, h1, tz) * terrain.HeightScale;
    }
}
