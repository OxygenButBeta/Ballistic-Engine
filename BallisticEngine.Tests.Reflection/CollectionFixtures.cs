using System.Collections.Generic;
using System.Numerics;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the G2 "collections" suite. A component carrying serializable collection members of every
// supported shape: a List<primitive>, a List<math-struct> (the LineRenderer.Points case), an array, a
// Dictionary<primitive,primitive>, and a List of scene-object refs (each element recurses through the same
// value pipeline). Before G2 every one of these deserialized to null (round-trip loss); after G2 they
// round-trip. A plain int leaf rides alongside to prove non-collection members are untouched.
public sealed class CollectionHolderBehaviour : Behaviour {
    public List<int> Ints = new() { 1, 2, 3 };                       // List<primitive>
    public List<Vector3> Points = new();                            // List<math-struct> (LineRenderer.Points shape)
    public string[] Names = { "a", "b" };                          // array of primitives
    public Dictionary<string, int> Scores = new();                 // Dictionary<primitive,primitive>
    public List<EntityRef> Targets = new();                        // List<scene-object ref> (element recursion)
    public List<int> EmptyList = new();                            // authored EMPTY list -> round-trips empty, not null
    public int Marker = 7;                                          // a plain leaf alongside the collections
}
