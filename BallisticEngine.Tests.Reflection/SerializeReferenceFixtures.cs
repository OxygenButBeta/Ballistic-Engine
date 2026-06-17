using System.Numerics;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the G3 "[SerializeReference] polymorphism" suite. A member whose DECLARED type is an
// interface or abstract class, marked [SerializeReference], must round-trip its LIVE concrete type: the
// serializer writes a $type tag (the concrete type's FullName) plus the instance's members, and the
// deserializer resolves the concrete type via TypeCache, instantiates it, and refills its members
// (recursively -- a nested polymorphic member rehydrates the same way). A null ref writes no $type (and
// no value). A plain leaf rides alongside to prove non-polymorphic members are byte-identical.

// ── Interface case: an interface declared type with several concrete implementors ──────────────────────
public interface IDamageModifier {
    int Order { get; set; }
}

public sealed class CritModifier : IDamageModifier {
    public int Order { get; set; }
    public float Multiplier = 1f;        // a primitive leaf member of the concrete type
}

public sealed class PoisonModifier : IDamageModifier {
    public int Order { get; set; }
    public int Dps = 5;
    public Vector3 Tint = new(0.2f, 0.8f, 0.1f);   // a math-struct member (recurses through the same pipeline)
}

// A concrete type with a NESTED polymorphic member -- proves the recursion: serializing this writes a
// $type for the OUTER type AND, for its Inner field, a nested $type for whatever concrete type Inner holds.
public sealed class CompositeModifier : IDamageModifier {
    public int Order { get; set; }
    public string Label = "composite";
    [SerializeReference] public IDamageModifier Inner;   // recursive polymorphism
}

// ── Abstract-base case: an abstract declared type with concrete subclasses ─────────────────────────────
public abstract class StatusEffect {
    public float Duration = 1f;
}

public sealed class BurnEffect : StatusEffect {
    public int Stacks = 1;
}

public sealed class FreezeEffect : StatusEffect {
    public float SlowFactor = 0.5f;
}

// ── The holder component carrying the [SerializeReference] members ─────────────────────────────────────
public sealed class PolymorphicHolderBehaviour : Behaviour {
    [SerializeReference] public IDamageModifier Mod;     // interface declared type -> concrete CritModifier/...
    [SerializeReference] public StatusEffect Effect;     // abstract declared type  -> concrete BurnEffect/...
    [SerializeReference] public IDamageModifier Unset;   // null -> no $type, no value written
    public int Marker = 7;                                // a plain leaf alongside (byte-identical)
}
