using BallisticEngine;
using OpenTK.Mathematics;

namespace BallisticEngine.Tests.Reflection;

// Sample types exercising every branch of the P0.2 property model: category classification, the two
// artifacts (TypePlan static / PropertyNode tree dynamic), multi-target + mixed-value + broadcast, and the
// cycle/depth guard. Each member/type names what it proves so a failed check points at the exact rule.

// A plain nested struct — the user's `struct Pair` Rule-2 example. No marker needed; recurses to its int
// members. A value type, so it can never form a reference cycle.
public struct SamplePair {
    public int X { get; set; }
    public int Y { get; set; }
}

// A plain nested CLASS — recurses like the struct, but is a reference type (relevant to the cycle guard).
public sealed class SampleNestedClass {
    public float A { get; set; }
    public string B { get; set; }
}

// Exercises the leaf categories + ordering. PropertyOrder pulls `Last` above declaration order; the rest
// keep declaration order (stable tie-break on equal order 0).
public sealed class SampleLeaves {
    public bool Flag { get; set; }
    public int Count { get; set; }
    public float Amount { get; set; }
    public string Label { get; set; }
    public SampleEnum Mode { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }

    [PropertyOrder(-10)] public int First { get; set; }   // floats to the very top
    [PropertyOrder(10)] public int Last { get; set; }     // sinks to the bottom

    public SamplePair Pair { get; set; }                   // Nested (recurses)
    public SampleNestedClass Child { get; set; }           // Nested class (recurses)

    [HideInInspector] public int Hidden { get; set; }      // excluded from the plan (InspectorMembers)

    public System.Action Callback { get; set; }            // Unsupported (delegate)
}

public enum SampleEnum { Off, Low, High }

// Multi-target fixture: two instances let the harness prove HasMultipleValues + broadcast SetValue.
public sealed class SampleMultiTarget {
    public int Value { get; set; }
    public string Tag { get; set; }
}

// Cycle fixture: a self-referential class. `Self` pointing back at an ancestor must hit the cycle guard,
// not recurse forever. `Other` to a DIFFERENT instance is a normal nested recursion.
public sealed class SampleNode {
    public int Id { get; set; }
    public SampleNode Self { get; set; }
    public SampleNode Other { get; set; }
}

// Polymorphic fixture: a [SerializeReference] interface member. Classifies as Polymorphic; with a concrete
// value assigned the tree recurses into that concrete type's members.
public interface ISampleModifier { }
public sealed class SampleCritModifier : ISampleModifier {
    public float Multiplier { get; set; }
}
public sealed class SamplePolyHost {
    [SerializeReference] public ISampleModifier Modifier { get; set; }
    public ISampleModifier Unmarked { get; set; }          // no marker → Unsupported (can't instantiate)

    // Abstract-BObject ASSET members (bug 2026-06-18): the engine's asset base types are ABSTRACT
    // (Texture3D / Texture2D / Mesh / ...; concrete bodies live in the backend, e.g. Dx12Texture3D). An
    // asset member must classify AssetRef — NEVER Polymorphic — even WITH [SerializeReference], because an
    // asset is referenced by guid, never type-swapped + instantiated. This is the engine contract the
    // editor's PolymorphicDrawer relies on (it must NOT steal an abstract-asset member from the asset slot
    // and expand the backend object's internal fields — the user-reported "Cubemap opens UID/Type/Sky").
    public Texture3D Cubemap { get; set; }
    [SerializeReference] public Texture3D MarkedCubemap { get; set; }
}

// Collection fixture: a List member classifies as Collection (recursion is Phase G2; the model only needs
// to NAME it correctly today).
public sealed class SampleCollectionHost {
    public System.Collections.Generic.List<int> Numbers { get; set; }
    public int[] Array { get; set; }
}
