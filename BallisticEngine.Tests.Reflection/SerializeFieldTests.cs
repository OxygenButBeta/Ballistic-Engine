using System.Linq;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// [SerializeField] (Unity parity, 2026-06-18): a NON-PUBLIC field opts into scene serialization AND the
// inspector when marked [SerializeField]; unmarked non-public fields stay invisible (the default). Public
// fields/properties are unchanged (no marker needed), so every existing component is byte-identical. This
// suite locks ComponentReflection.SerializableMembers / InspectorMembers behaviour around the marker:
//   - a private field WITH [SerializeField] is serializable + inspector-visible
//   - a protected field WITH [SerializeField] is serializable
//   - a private field WITHOUT the marker is excluded from both
//   - a public field is included regardless (the marker is a no-op there)
//   - [SerializeField] + [HideInInspector] serializes but hides
//   - [SerializeField] + [NotSerialized] is excluded from both ([NotSerialized] wins)
//   - compiler-generated auto-property backing fields (now visible to the NonPublic scan) are NOT leaked
// Pure reflection over the fixture — no scene, no ImGui — so it runs anywhere in Program.cs.
internal static class SerializeFieldTests {
    public static int Run() {
        var h = new Harness();

        var ser = ComponentReflection.SerializableMembers(typeof(SerializeFieldHost))
            .Select(m => m.Name).ToHashSet();
        var insp = ComponentReflection.InspectorMembers(typeof(SerializeFieldHost))
            .Select(m => m.Name).ToHashSet();

        // Opt-in: non-public + [SerializeField] → serialized AND shown.
        h.Check("private [SerializeField] → serializable", ser.Contains("privateMarked"));
        h.Check("private [SerializeField] → inspector-visible", insp.Contains("privateMarked"));
        h.Check("protected [SerializeField] → serializable", ser.Contains("protectedMarked"));

        // Default: non-public WITHOUT the marker stays invisible (the pre-existing rule).
        h.Check("private unmarked → NOT serializable", !ser.Contains("privateUnmarked"));
        h.Check("private unmarked → NOT inspector-visible", !insp.Contains("privateUnmarked"));

        // Public members are unchanged — the marker is unnecessary (and harmless) there.
        h.Check("public field → serializable (no marker needed)", ser.Contains("PublicField"));
        h.Check("public property → serializable", ser.Contains("PublicProp"));

        // Attribute interactions: [HideInInspector] serializes but hides; [NotSerialized] wins (excludes both).
        h.Check("[SerializeField]+[HideInInspector] → serializable", ser.Contains("markedHidden"));
        h.Check("[SerializeField]+[HideInInspector] → hidden from inspector", !insp.Contains("markedHidden"));
        h.Check("[SerializeField]+[NotSerialized] → NOT serializable ([NotSerialized] wins)",
            !ser.Contains("markedNotSerialized"));
        h.Check("[SerializeField]+[NotSerialized] → NOT inspector-visible", !insp.Contains("markedNotSerialized"));

        // The NonPublic scan must not leak compiler-generated backing fields of auto-properties.
        h.Check("auto-property backing field NOT leaked",
            !ser.Any(n => n.Contains("k__BackingField") || n.Contains("BackingField")));

        return h.Report("SerializeField (Unity parity)");
    }
}

// Fixture: a component-like type with the full matrix of (public/private/protected) × (marked/unmarked) ×
// (HideInInspector/NotSerialized) fields plus auto-properties (their backing fields must not leak).
internal class SerializeFieldHost : Behaviour {
    [SerializeField] int privateMarked;
    [SerializeField] protected float protectedMarked;
    int privateUnmarked;                                   // no marker → invisible
    public int PublicField;                                // public → serialized as before
    public string PublicProp { get; set; }                 // auto-prop: backing field must not leak

    [SerializeField, HideInInspector] int markedHidden;        // serialized but hidden
    [SerializeField, NotSerialized] int markedNotSerialized;   // [NotSerialized] wins → excluded from both

    // Reference the private fields so the compiler doesn't warn them unused (and to make intent explicit).
    public int Touch() => privateMarked + privateUnmarked + markedHidden + markedNotSerialized + (int)protectedMarked;
}
