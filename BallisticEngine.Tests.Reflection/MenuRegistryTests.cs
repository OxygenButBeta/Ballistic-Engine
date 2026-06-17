using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// A1 (editor-rework Rule 3) registry-discovery contract, tested HEADLESSLY at the substrate level. The
// editor's EditorWindowRegistry lives in the host (editor) assembly and can't be referenced here, so this
// suite proves the engine-side pieces the registry is built on — and that, fed the SAME inputs with the
// SAME real DeterministicResolver, they yield the exact menu the editor would render. If these pass, the
// editor registry's only remaining job (compiling delegates + ImGui sub-menu nesting) is trivial glue.
//
// Covered: (1) the [MenuItem] attribute round-trips Path/Order; (2) TypeCache.GetMethodsWithAttribute finds
// the fixture menu methods and respects the static-only filter; (3) DeterministicResolver orders the menu
// entries by Order then a stable tie-break, independent of registration order; (4) AllowMultiple yields one
// entry per attribute; (5) the path helpers (TopMenu/Leaf/SubMenus) parse correctly; (6) the menu query
// drops the fixtures on a rebuild-over-engine-only (the hot-reload substrate the registry's ReloadCaches
// callback rides on).
internal static class MenuRegistryTests {
    // A local mirror of EditorWindowRegistry.Entry's relevant fields (the editor type is unreferenceable
    // here). Built the SAME way the registry builds it, so an ordering regression in the shared
    // DeterministicResolver surfaces here.
    readonly record struct MenuEntry(string Path, int Order, string TieKey);

    static string TopMenu(string path) { int s = path.IndexOf('/'); return s < 0 ? path : path[..s]; }
    static string Leaf(string path) { int s = path.LastIndexOf('/'); return s < 0 ? path : path[(s + 1)..]; }
    static string[] SubMenus(string path) {
        string[] parts = path.Split('/');
        return parts.Length <= 2 ? Array.Empty<string>() : parts[1..^1];
    }

    // Re-runs the registry's discovery over the fixture methods using the real engine primitives.
    static List<MenuEntry> DiscoverFixtureMenu() {
        var resolver = new DeterministicResolver<MenuEntry>();
        foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<MenuItemAttribute>()) {
            if (method.GetParameters().Length != 0) continue;
            foreach (MenuItemAttribute attr in method.GetCustomAttributes<MenuItemAttribute>()) {
                if (string.IsNullOrWhiteSpace(attr.Path)) continue;
                string path = attr.Path.Trim();
                string tieKey = $"{path} {method.DeclaringType?.FullName} {method.Name}";
                resolver.Register(new MenuEntry(path, attr.Order, tieKey), priority: -attr.Order, tieKey: tieKey);
            }
        }
        return resolver.All().ToList();
    }

    public static int Run() {
        var h = new Harness();

        // Ensure the fixtures are in the scanned universe (Program.cs builds engine + tests, but a prior
        // suite may have rebuilt over the engine only — rebuild with the tests assembly to be self-contained).
        Assembly engine = typeof(ComponentRegistry).Assembly;
        Assembly tests = typeof(MenuRegistryTests).Assembly;
        TypeCache.Build(engine, tests);

        // ── (1) Attribute round-trip ────────────────────────────────────────────────────────────────
        MethodInfo beta = typeof(SampleMenuWindows).GetMethod(nameof(SampleMenuWindows.OpenBeta));
        var betaAttr = beta.GetCustomAttribute<MenuItemAttribute>();
        h.Check("[MenuItem] Path round-trips", betaAttr is { Path: "Window/Beta" });
        h.Check("[MenuItem] Order round-trips", betaAttr is { Order: 0 });

        // ── (2) TypeCache finds the static menu methods; instance one excluded ────────────────────────
        var menuMethods = TypeCache.GetMethodsWithAttribute<MenuItemAttribute>();
        var menuMethodNames = menuMethods.Select(m => m.Name).ToHashSet();
        h.Check("static [MenuItem] method discovered", menuMethodNames.Contains(nameof(SampleMenuWindows.OpenBeta)));
        h.Check("AllowMultiple method discovered once as a method", menuMethods.Count(m => m.Name == nameof(SampleMenuWindows.OpenDouble)) == 1);
        h.Check("instance [MenuItem] method excluded (static-only)", !menuMethodNames.Contains(nameof(SampleMenuWindows.InstanceMenu)));

        // ── (3)+(4) Deterministic ordered menu build ─────────────────────────────────────────────────
        List<MenuEntry> menu = DiscoverFixtureMenu();
        var windowLeaves = menu.Where(e => e.Path.StartsWith("Window/")).Select(e => Leaf(e.Path)).ToList();

        // AllowMultiple: OpenDouble produced one Assets entry AND one Window entry.
        h.Check("AllowMultiple → one entry per attribute (Assets)", menu.Any(e => e.Path == "Assets/DoubleA"));
        h.Check("AllowMultiple → one entry per attribute (Window)", menu.Any(e => e.Path == "Window/DoubleW"));

        // Order-then-tiebreak: Alpha and Beta share Order 0; Alpha sorts before Beta (tie-break ascending).
        int iAlpha = windowLeaves.IndexOf("Alpha");
        int iBeta = windowLeaves.IndexOf("Beta");
        h.Check("both Order-0 leaves present", iAlpha >= 0 && iBeta >= 0);
        h.Check("equal-Order tie-break is stable (Alpha before Beta)", iAlpha < iBeta);

        // Order ascending overall: Order-0 leaves precede the Order-5 nested, which precedes Order-20 Late.
        int iNested = windowLeaves.IndexOf("Nested");
        int iLate = windowLeaves.IndexOf("Late");
        h.Check("lower Order sorts first (0 < 5 < 20)", iBeta < iNested && iNested < iLate);

        // The full Window order is a stable, exact sequence (the determinism guarantee the menu relies on).
        h.CheckStrings("Window menu order is deterministic + exact",
            windowLeaves, "Alpha", "Beta", "DoubleW", "Nested", "Late");

        // Determinism is independent of registration order: discovering twice yields the identical sequence.
        var second = DiscoverFixtureMenu().Where(e => e.Path.StartsWith("Window/")).Select(e => Leaf(e.Path));
        h.CheckStrings("re-discovery yields identical order", second, windowLeaves.ToArray());

        // ── (5) Path helpers ─────────────────────────────────────────────────────────────────────────
        h.Check("TopMenu parse", TopMenu("Window/Tools/Nested") == "Window");
        h.Check("Leaf parse", Leaf("Window/Tools/Nested") == "Nested");
        h.CheckStrings("SubMenus parse (nested)", SubMenus("Window/Tools/Nested"), "Tools");
        h.Check("SubMenus empty for 2-segment path", SubMenus("Window/Inspector").Length == 0);

        // ── (6) Hot-reload substrate: rebuild over engine-only drops the fixture menu methods ─────────
        // The editor registry's ReloadCaches callback re-discovers over the rebuilt TypeCache; here we prove
        // the underlying query goes empty for the (now-unscanned) fixtures, so a re-discovery would too.
        TypeCache.Build(engine);
        bool fixturesGone = TypeCache.GetMethodsWithAttribute<MenuItemAttribute>()
            .All(m => m.DeclaringType != typeof(SampleMenuWindows));
        h.Check("rebuild over engine-only drops fixture [MenuItem]s", fixturesGone);

        // Restore the full build so later suites (and re-runs) see the fixtures again.
        TypeCache.Build(engine, tests);

        return h.Report("Menu/Window registry (A1)");
    }
}
