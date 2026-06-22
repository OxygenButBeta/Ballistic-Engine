namespace BallisticEngine;

public readonly struct LagRaycastHit {
    public readonly NetworkObject Pawn;
    public readonly float Distance;
    public readonly Vector3 Point;
    public LagRaycastHit(NetworkObject pawn, float distance, Vector3 point) {
        Pawn = pawn; Distance = distance; Point = point;
    }
}
