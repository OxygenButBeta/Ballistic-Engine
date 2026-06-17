using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the G0 "loud drops" suite. A component whose member HOLDS a value the serializer cannot
// write (a scene-object reference with no asset guid) must produce a LOUD warning instead of silently
// vanishing on save/load (the §3.45 silent-failure trap). These mirror the real shape: an Entity/Behaviour
// field is a BObject with no AssetDatabase guid, so SerializeValue returns null and the member is dropped.

// The component under test. `Linked` is a scene-object reference (a Behaviour = a BObject with no asset
// guid) → it serializes to null and must be reported. `Healthy` is a plain int that round-trips fine → it
// must NEVER be reported (the warning is for genuine drops only).
public sealed class DropFixtureBehaviour : Behaviour {
    public Behaviour Linked;   // BObject ref, no guid → dropped → LOUD
    public int Healthy = 7;    // round-trips → never reported
}

// A second trivial component used as the drop target (something for `Linked` to point at). Its own members
// round-trip, so it never triggers a drop warning of its own.
public sealed class DropTargetBehaviour : Behaviour {
    public int Marker = 3;
}
