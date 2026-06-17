using System.Numerics;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the G4 "nested struct/class member round-trip" suite. A member whose declared type is a plain
// concrete class or non-primitive struct (NOT [SerializeReference], NOT a math struct / asset / collection /
// ref) must round-trip its serializable members as a nested YAML mapping -- with NO $type tag (the declared
// type IS the concrete type). Before G4 such a member serialized via pass-through but deserialized to null
// (the round-trip was lost); now it round-trips. The suite proves: a struct member round-trips its inner
// fields (the struct WRITE-BACK the codec must do via boxed unbox), a class member round-trips, nested-in-
// nested recurses, a null member writes nothing, a plain leaf alongside is untouched, and a self-referential
// class hits the cycle guard instead of recursing forever.

// ── A plain nested STRUCT (the user's `struct Pair` Rule-2 example) ─────────────────────────────────────
// Carries an int leaf, a math-struct member (recurses through the converter), AND a nested struct-in-struct
// (Inner) -- so a single Settings value exercises leaf + math + nested-in-nested in one round-trip.
public struct NestedSettings {
    public int Level { get; set; }
    public Vector3 Offset { get; set; }
    public InnerRange Inner { get; set; }   // nested struct inside the struct (nested-in-nested)
}

public struct InnerRange {
    public float Min { get; set; }
    public float Max { get; set; }
}

// ── A plain nested CLASS ─────────────────────────────────────────────────────────────────────────────────
// A reference type (relevant to the cycle guard). Carries a primitive + a string + a nested STRUCT child, so
// a class round-trip also drives the struct path one level down.
public sealed class NestedConfig {
    public string Name { get; set; } = "cfg";
    public int Count { get; set; }
    public InnerRange Bounds { get; set; }
}

// ── A self-referential class for the cycle guard ─────────────────────────────────────────────────────────
// `Next` to a DIFFERENT instance is a normal nested recursion; `Next` forming a cycle (A.Next = B, B.Next = A)
// must stop at the guard (serialize the back-reference as null), not recurse forever.
public sealed class NestedLink {
    public int Id { get; set; }
    public NestedLink Next { get; set; }
}

// ── The holder component carrying the plain nested members ───────────────────────────────────────────────
public sealed class NestedHolderBehaviour : Behaviour {
    public NestedSettings Settings { get; set; }   // nested STRUCT  -> inner fields round-trip (write-back)
    public NestedConfig Config { get; set; }       // nested CLASS   -> members round-trip
    public NestedConfig Unset { get; set; }        // null class     -> writes nothing
    public NestedLink Chain { get; set; }          // self-ref class -> cycle guard
    public int Marker = 11;                         // a plain leaf alongside (byte-identical)
}
