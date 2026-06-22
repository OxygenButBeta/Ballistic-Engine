namespace BallisticEngine.Tests.Reflection;

// Fixture methods carrying the engine-side [MenuItem] attribute (the editor-rework Rule-3 window-discovery
// shape). The editor's EditorWindowRegistry is editor-side and can't be referenced from this engine-only
// harness, so these fixtures exercise the SUBSTRATE the registry is built on — TypeCache.GetMethodsWithAttribute
// + MenuItemAttribute round-trip + DeterministicResolver ordering — which is the part that could silently
// break the menu. The registry itself is thin glue over these proven primitives.
//
// The set is deliberately laid out to prove ordering: two leaves share Order 0 (tie-break must be stable),
// one entry carries TWO [MenuItem]s (AllowMultiple → two menu entries from one method), and Orders span a
// gap so a group divider rule could be exercised.
//
// NOT a static class — it hosts a non-static [MenuItem] method the static-only discovery must skip.
public class SampleMenuWindows {
    // Order 0, leaf "Beta" — shares Order with Alpha to force a tie-break.
    [MenuItem("Window/Beta", 0)]
    public static void OpenBeta() { }

    // Order 0, leaf "Alpha" — same Order as Beta; deterministic order must put Alpha before Beta.
    [MenuItem("Window/Alpha", 0)]
    public static void OpenAlpha() { }

    // Order 5, a nested path — proves SubMenus parsing ("Window" top, "Tools" sub, "Nested" leaf).
    [MenuItem("Window/Tools/Nested", 5)]
    public static void OpenNested() { }

    // Order 20 — a big gap from Order 5 (the group-divider rule fires here).
    [MenuItem("Window/Late", 20)]
    public static void OpenLate() { }

    // One method, TWO menu entries (AllowMultiple). Proves a single method yields one entry per attribute.
    [MenuItem("Assets/DoubleA", 1)]
    [MenuItem("Window/DoubleW", 1)]
    public static void OpenDouble() { }

    // A non-static [MenuItem] method — must be IGNORED by the static-only discovery (defensive: the
    // attribute targets methods generally; the registry/TypeCache only invoke statics).
    [MenuItem("Window/ShouldNotAppear", 0)]
    public void InstanceMenu() { }
}
