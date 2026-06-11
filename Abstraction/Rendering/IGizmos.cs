using OpenTK.Mathematics;

namespace BallisticEngine;

// Editor-gizmo drawing surface handed to a component's OnDrawGizmos/OnDrawGizmosSelected. The
// engine defines the interface (pure OpenTK math, NO ImGui/GL); the editor implements it against
// its draw list + camera so components can paint scene-view handles without depending on the
// editor. Color is mutable state applied to subsequent draws (Unity's Gizmos.color pattern).
public interface IGizmos {
    Vector3 Color { get; set; }

    void DrawLine(Vector3 from, Vector3 to);

    // A line from origin along direction (length = direction's magnitude).
    void DrawRay(Vector3 origin, Vector3 direction);

    void DrawWireSphere(Vector3 center, float radius);

    // Cone with its apex at `apex` opening along `direction` (length = height), with the given
    // half-angle in degrees at the base. Used by spot lights.
    void DrawWireCone(Vector3 apex, Vector3 direction, float halfAngleDegrees);

    // Axis-aligned-in-local wire box: `center` + `size` rotated by `rotation`.
    void DrawWireCube(Vector3 center, Vector3 size, Quaternion rotation);

    // A small camera-facing billboard marker (e.g. a light bulb / camera icon) at a world point.
    void DrawIcon(Vector3 center, GizmoIcon icon);
}

public enum GizmoIcon {
    Light,
    Camera,
}
