using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// A4 (editor-rework) substrate contract, tested HEADLESSLY. The editor's EditorInputRouter binds the real
// hotkeys (and lives in the host assembly, unreferenceable here), but the RESOLUTION substrate it rides on —
// InputActionChain<TKey> — is engine-side pure logic and IS referenceable. This suite proves the load-bearing
// guarantees the input shell depends on, with a FAKE key probe (set membership) the same way OrderedPassListTests
// threads a fake run delegate:
//   (1) priority resolution — the highest-priority ACTIVE binding wins;
//   (2) stable Id tie-break — equal priority resolves by ordinal Id, independent of registration order;
//   (3) context masking / NO LEAK — a SceneView binding cannot fire while only Global is in scope, and the
//       combined mask selects the right one (the editor's reason for the context model);
//   (4) EXACT-modifier chord match — the real editor disambiguations: Ctrl+R (global rebuild) vs bare-R (gizmo
//       scale), and F vs Ctrl+Shift+F (the old fall-through-to-plain-Frame bug);
//   (5) enabled gate — a disabled action does not win;
//   (6) Dispatch invokes the winner exactly once (the side effect);
//   (7) CheckConflicts — flags same-chord+context+priority; a clean table (the editor's real shape) reports none;
//   (8) Build / Add-after-Build re-freeze / Clear / null guards.
// If these pass, the editor's only remaining job (binding real action bodies behind contexts) is trivial glue —
// the same posture as the A1 menu-registry and A2 pass-list suites.
internal static class InputActionChainTests {
    // A fake key enum standing in for OpenTK Keys (the substrate never names a concrete key). Mirrors the
    // editor's set: W/E/R gizmo keys, F frame, C/V clipboard, plus a couple for guard tests.
    enum K { W, E, R, F, C, V, S, Z }

    // The editor's three real contexts as bit flags (1/2/4), so the tests exercise the exact masking the router
    // builds. Names mirror EditorInputContext.{Global, SceneView, SceneViewHovered}.
    const int Global = 1 << 0;
    const int SceneView = 1 << 1;
    const int Hovered = 1 << 2;

    static InputActionChain<K> NewChain() => new();

    // A chord-active probe backed by a single "currently pressed" chord (edge semantics: exactly one chord fires
    // per frame). Returns true only for the chord that equals the pressed one — exact modifier match included,
    // since KeyChord equality compares Ctrl/Shift/Alt. This is the test analogue of EditorInputRouter.IsChordActive
    // reading raw OpenTK edge + modifiers.
    static Func<KeyChord<K>, bool> Pressed(KeyChord<K> chord) => c => c.Equals(chord);

    public static int Run() {
        var h = new Harness();

        // ── (1) Priority: the highest-priority ACTIVE binding wins ───────────────────────────────────
        // Two bindings on the SAME chord+context but different priority; the higher one wins.
        {
            string fired = null;
            var chain = NewChain()
                .Add("low", new KeyChord<K>(K.W), Global, priority: 0, () => fired = "low")
                .Add("high", new KeyChord<K>(K.W), Global, priority: 10, () => fired = "high");
            chain.Dispatch(Global, Pressed(new KeyChord<K>(K.W)));
            h.Check("highest priority active binding wins", fired == "high", $"fired '{fired}'");
        }

        // ── (2) Stable Id tie-break, registration-order-independent ──────────────────────────────────
        // Equal priority + same chord/context → the lower ordinal Id wins, regardless of which was added first.
        {
            var a = NewChain()
                .Add("bbb", new KeyChord<K>(K.W), Global, 0, () => { })
                .Add("aaa", new KeyChord<K>(K.W), Global, 0, () => { });
            var b = NewChain()
                .Add("aaa", new KeyChord<K>(K.W), Global, 0, () => { })
                .Add("bbb", new KeyChord<K>(K.W), Global, 0, () => { });
            h.Check("equal-priority tie resolves to lowest Id (added b,a)",
                a.Resolve(Global, Pressed(new KeyChord<K>(K.W)))?.Id == "aaa");
            h.Check("equal-priority tie is registration-order-independent (added a,b)",
                b.Resolve(Global, Pressed(new KeyChord<K>(K.W)))?.Id == "aaa");
        }

        // ── (3) Context masking / NO LEAK ────────────────────────────────────────────────────────────
        // A SceneView binding must NOT fire when only Global is in scope (the core no-leak property), and the
        // combined mask must select it.
        {
            var chain = NewChain()
                .Add("sceneOnly", new KeyChord<K>(K.F), SceneView, 0, () => { });
            h.Check("SceneView binding does NOT fire in Global-only scope",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.F))) is null);
            h.Check("SceneView binding fires when SceneView is in the mask",
                chain.Resolve(Global | SceneView, Pressed(new KeyChord<K>(K.F)))?.Id == "sceneOnly");
        }

        // A Hovered binding (gizmo keys) must not fire when only SceneView is live (cursor not over the image),
        // but fires when both SceneView and Hovered are in scope.
        {
            var chain = NewChain()
                .Add("gizmoScale", new KeyChord<K>(K.R), Hovered, 0, () => { });
            h.Check("Hovered binding does NOT fire in SceneView-only scope",
                chain.Resolve(SceneView, Pressed(new KeyChord<K>(K.R))) is null);
            h.Check("Hovered binding fires when Hovered is in the mask",
                chain.Resolve(SceneView | Hovered, Pressed(new KeyChord<K>(K.R)))?.Id == "gizmoScale");
        }

        // ── (4) EXACT-modifier chord match: the real editor disambiguations ─────────────────────────
        // The editor's actual conflict pair: Ctrl+R (global rebuild) and bare-R (scene gizmo scale). Build the
        // exact table shape and assert each chord resolves to the right action in the right context.
        {
            var chain = NewChain()
                .Add("rebuild", new KeyChord<K>(K.R, ctrl: true), Global, 0, () => { })
                .Add("gizmoScale", new KeyChord<K>(K.R), Hovered, 0, () => { });

            // Ctrl+R in the global dispatch → rebuild (NOT gizmo: gizmo's chord is bare-R, exact match fails).
            h.Check("Ctrl+R resolves to rebuild (global)",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.R, ctrl: true)))?.Id == "rebuild");
            // Ctrl+R in the scene dispatch → nothing (rebuild is Global-only, gizmo wants bare-R) — so Ctrl+R
            // can never trigger gizmo-scale, the old `!KeyCtrl` guard, now structural.
            h.Check("Ctrl+R does NOT trigger gizmo-scale in scene scope",
                chain.Resolve(SceneView | Hovered, Pressed(new KeyChord<K>(K.R, ctrl: true))) is null);
            // Bare R in the scene dispatch → gizmo scale.
            h.Check("bare R resolves to gizmo-scale (hovered)",
                chain.Resolve(SceneView | Hovered, Pressed(new KeyChord<K>(K.R)))?.Id == "gizmoScale");
        }

        // The F vs Ctrl+Shift+F pair — the bug where Ctrl+Shift+F fell through to plain Frame. Exact-modifier
        // match means the F chord (no modifiers) does NOT match a Ctrl+Shift+F press, and vice-versa.
        {
            var chain = NewChain()
                .Add("frame", new KeyChord<K>(K.F), SceneView, 0, () => { })
                .Add("align", new KeyChord<K>(K.F, ctrl: true, shift: true), SceneView, 0, () => { });
            h.Check("plain F resolves to frame, not align",
                chain.Resolve(SceneView, Pressed(new KeyChord<K>(K.F)))?.Id == "frame");
            h.Check("Ctrl+Shift+F resolves to align, NOT falling through to frame",
                chain.Resolve(SceneView, Pressed(new KeyChord<K>(K.F, ctrl: true, shift: true)))?.Id == "align");
        }

        // ── (5) Enabled gate: a disabled action does not win ─────────────────────────────────────────
        {
            var chain = NewChain()
                .Add("save", new KeyChord<K>(K.S, ctrl: true), Global, 0, () => { });
            h.Check("enabled action resolves",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.S, ctrl: true)), _ => true)?.Id == "save");
            h.Check("disabled action does not resolve",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.S, ctrl: true)), _ => false) is null);
        }

        // ── (6) Dispatch invokes the winner exactly once; returns true/false ─────────────────────────
        {
            int count = 0;
            var chain = NewChain().Add("x", new KeyChord<K>(K.Z, ctrl: true), Global, 0, () => count++);
            bool hit = chain.Dispatch(Global, Pressed(new KeyChord<K>(K.Z, ctrl: true)));
            h.Check("Dispatch returns true on a hit", hit);
            h.Check("Dispatch invoked the winner exactly once", count == 1, $"count={count}");
            bool miss = chain.Dispatch(Global, Pressed(new KeyChord<K>(K.F)));   // no binding for F
            h.Check("Dispatch returns false on no match", !miss);
            h.Check("no extra invoke on a miss", count == 1, $"count={count}");
        }

        // ── (7) CheckConflicts: flags same chord+context+priority; clean table reports none ──────────
        {
            // The editor's REAL table shape is conflict-free: every binding differs in chord, context, or both.
            var clean = NewChain()
                .Add("rebuild", new KeyChord<K>(K.R, ctrl: true), Global, 0, () => { })
                .Add("gizmoScale", new KeyChord<K>(K.R), Hovered, 0, () => { })
                .Add("frame", new KeyChord<K>(K.F), SceneView, 0, () => { })
                .Add("align", new KeyChord<K>(K.F, ctrl: true, shift: true), SceneView, 0, () => { });
            h.Check("conflict-free table reports no conflicts", clean.CheckConflicts().Count == 0,
                $"got {clean.CheckConflicts().Count}");

            // Two bindings sharing chord+context+priority → flagged (only the Id tie-break would separate them).
            var dirty = NewChain()
                .Add("a", new KeyChord<K>(K.W), SceneView, 0, () => { })
                .Add("b", new KeyChord<K>(K.W), SceneView, 0, () => { });
            var conflicts = dirty.CheckConflicts();
            h.Check("same chord+context+priority flagged as a conflict", conflicts.Count == 1,
                $"got {conflicts.Count}");

            // Same chord+context but DIFFERENT priority is NOT a conflict (priority breaks it deterministically).
            var prioritized = NewChain()
                .Add("a", new KeyChord<K>(K.W), SceneView, 0, () => { })
                .Add("b", new KeyChord<K>(K.W), SceneView, 1, () => { });
            h.Check("different priority is not a conflict", prioritized.CheckConflicts().Count == 0);
        }

        // ── (8) Build / Add-after-Build re-freeze / Clear / null guards ──────────────────────────────
        {
            // Add after the first resolve (which builds lazily) still participates after the implicit rebuild.
            var chain = NewChain().Add("a", new KeyChord<K>(K.W), Global, 0, () => { });
            _ = chain.Resolve(Global, Pressed(new KeyChord<K>(K.E)));   // builds lazily, matches nothing
            chain.Add("b", new KeyChord<K>(K.E), Global, 5, () => { });  // higher priority, new chord
            h.Check("Add after a resolve re-freezes and the new binding participates",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.E)))?.Id == "b");

            chain.Clear();
            h.Check("Clear empties the chain", chain.Count == 0);
            h.Check("resolve on a cleared chain returns null",
                chain.Resolve(Global, Pressed(new KeyChord<K>(K.W))) is null);
        }

        // Null guards: Add(null action), Resolve(null probe).
        {
            bool threwNullAction = false;
            try { NewChain().Add((InputAction<K>)null); }
            catch (ArgumentNullException) { threwNullAction = true; }
            h.Check("Add(null action) throws", threwNullAction);

            bool threwNullId = false;
            try { _ = new InputAction<K>(null, new KeyChord<K>(K.W), Global, 0, () => { }); }
            catch (ArgumentNullException) { threwNullId = true; }
            h.Check("InputAction(null id) throws", threwNullId);

            bool threwNullInvoke = false;
            try { _ = new InputAction<K>("x", new KeyChord<K>(K.W), Global, 0, null); }
            catch (ArgumentNullException) { threwNullInvoke = true; }
            h.Check("InputAction(null invoke) throws", threwNullInvoke);

            bool threwNullProbe = false;
            try { NewChain().Resolve(Global, null); }
            catch (ArgumentNullException) { threwNullProbe = true; }
            h.Check("Resolve(null probe) throws", threwNullProbe);
        }

        // ── KeyChord value equality (a chord is a dictionary/HashSet key) ────────────────────────────
        {
            h.Check("equal chords are equal",
                new KeyChord<K>(K.F, ctrl: true).Equals(new KeyChord<K>(K.F, ctrl: true)));
            h.Check("chords differing in modifier are unequal",
                !new KeyChord<K>(K.F, ctrl: true).Equals(new KeyChord<K>(K.F)));
            h.Check("chords differing in key are unequal",
                !new KeyChord<K>(K.F).Equals(new KeyChord<K>(K.C)));
            h.Check("equal chords share a hash code",
                new KeyChord<K>(K.F, shift: true).GetHashCode() == new KeyChord<K>(K.F, shift: true).GetHashCode());
        }

        return h.Report("Input action chain (A4)");
    }
}
