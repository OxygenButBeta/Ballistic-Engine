
namespace BallisticEngine;

/// <summary>
/// One oriented bounding-box surface card (UE <c>FLumenCardOBB</c> equivalent), in MESH-LOCAL space
/// (the same space as <see cref="MeshData.Vertices"/> and <see cref="MeshSdf"/>).
///
/// <see cref="AxisX"/>/<see cref="AxisY"/> span the capture rectangle (the card's UV plane);
/// <see cref="AxisZ"/> is the capture/view direction — the card "looks" down -AxisZ, so AxisZ points
/// OUTWARD along the surface normal it represents. <see cref="Extent"/> is the half-size along each
/// of the three axes. <see cref="DirectionIndex"/> 0..5 encodes the dominant axis-aligned orientation:
///   axis = DirectionIndex / 2, sign = (DirectionIndex &amp; 1) ? +1 : -1, order -X,+X,-Y,+Y,-Z,+Z.
/// </summary>
public struct MeshCard {
    public Vector3 Origin;     // OBB center, mesh-local
    public Vector3 AxisX;      // unit, capture-plane U
    public Vector3 AxisY;      // unit, capture-plane V
    public Vector3 AxisZ;      // unit, capture/view normal (card faces -AxisZ)
    public Vector3 Extent;     // half-size along (AxisX, AxisY, AxisZ)
    public int DirectionIndex; // 0..5

    public MeshCard(Vector3 origin, Vector3 axisX, Vector3 axisY, Vector3 axisZ, Vector3 extent, int directionIndex) {
        Origin = origin;
        AxisX = axisX;
        AxisY = axisY;
        AxisZ = axisZ;
        Extent = extent;
        DirectionIndex = directionIndex;
    }
}

/// <summary>
/// CPU-side mesh-card representation for a single mesh (UE <c>FMeshCardsBuildData</c> equivalent),
/// generated offline at import time (Lumen FAZ 3a). A small set of oriented bounding boxes that a
/// later surface cache will capture/light. Built from the per-mesh <see cref="MeshSdf"/> surfels, so
/// cards are only present when an SDF was generated. Null for skinned meshes, when card generation is
/// disabled, and for v8-and-earlier artifacts — existing code paths default this to null.
/// </summary>
public sealed class MeshCards {
    public MeshCard[] Cards;

    public MeshCards() { }

    public MeshCards(MeshCard[] cards) { Cards = cards; }

    public bool IsValid => Cards is { Length: > 0 };

    public int Count => Cards?.Length ?? 0;
}
