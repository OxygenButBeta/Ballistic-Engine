namespace BallisticEngine;

public static class LagHitbox {
    public static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t) {
        t = 0f;
        Vector3 oc = origin - center;
        float b = Vector3.Dot(oc, dir);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;
        if (disc < 0f) return false;
        float sq = MathF.Sqrt(disc);
        float t0 = -b - sq;
        if (t0 >= 0f) { t = t0; return true; }
        float t1 = -b + sq;
        if (t1 >= 0f) { t = t1; return true; }

        return false;
    }
}
