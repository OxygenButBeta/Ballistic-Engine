using System.Collections.Generic;
using System.Numerics;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the RW8 / EF15 "polymorphic collection round-trip" suite. A `[SerializeReference] List<IFoo>`
// (and `IFoo[]`) must round-trip the LIVE concrete type of EVERY element: the serializer writes a per-element
// $type tag (the concrete FullName) plus that element's members; the deserializer resolves each element's
// concrete type via TypeCache, instantiates it, and refills its members (the SAME recursion a scalar
// [SerializeReference] member uses). Before RW8 the collection serializer used SerializeValue per element, so
// the elements lost their $type and deserialized to the wrong/null shape -- the long-standing RW8 gap.
//
// Reuses the G3 polymorphic types where it can (IDamageModifier + CritModifier/PoisonModifier/CompositeModifier),
// and adds an abstract-base list element type to prove the abstract path as well as the interface one. A
// non-polymorphic list rides alongside to prove List<int>/List<Material>-shaped members stay byte-identical
// (no $type written).

// ── Abstract-base list element (mirrors the G3 StatusEffect family but used in a list) ──────────────────
public abstract class Shape {
    public float Area = 1f;
}

public sealed class Circle : Shape {
    public float Radius = 2f;
}

public sealed class Square : Shape {
    public float Side = 3f;
    public Vector3 Tint = new(0.1f, 0.2f, 0.3f);   // a math-struct member (recurses through the same pipeline)
}

// The holder carrying the polymorphic collection members + a non-polymorphic list control.
public sealed class PolymorphicCollectionHolderBehaviour : Behaviour {
    [SerializeReference] public List<IDamageModifier> Mods = new();   // interface element list -> per-element $type
    [SerializeReference] public Shape[] Shapes = System.Array.Empty<Shape>(); // abstract element ARRAY -> per-element $type
    public List<int> PlainInts = new();                              // non-polymorphic control -> NO $type, byte-identical
    public int Marker = 7;                                            // a plain leaf alongside
}
