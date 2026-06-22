
namespace BallisticEngine;

public sealed record MeshShape(Vector3[] Vertices, uint[] Indices, Vector3 Scale) : PhysicsShape;
