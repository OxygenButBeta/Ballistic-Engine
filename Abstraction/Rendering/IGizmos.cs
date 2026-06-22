
namespace BallisticEngine;

public interface IGizmos {
    Vector3 Color { get; set; }

    Vector3 CameraPosition { get; }

    void DrawLine(Vector3 from, Vector3 to);

    void DrawRay(Vector3 origin, Vector3 direction);

    void DrawWireSphere(Vector3 center, float radius);

    void DrawSolidSphere(Vector3 center, float radius);

    void DrawWireCone(Vector3 apex, Vector3 direction, float halfAngleDegrees);

    void DrawWireCube(Vector3 center, Vector3 size, Quaternion rotation);

    void DrawIcon(Vector3 center, GizmoIcon icon);
}
