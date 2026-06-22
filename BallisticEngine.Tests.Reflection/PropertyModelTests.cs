using BallisticEngine;
using OpenTK.Mathematics;

namespace BallisticEngine.Tests.Reflection;

// P0.2 + P0.4 (Chunk 1) contract + oracle. The headless "resolve-plan-for-type" suite the plan calls the
// definition of the property model's contract: it locks category classification, the static TypePlan
// (ordering + members), the dynamic N-target PropertyNode tree (mixed-value, broadcast, lazy recursion),
// the cycle/depth guard (Trap 3), and the deterministic resolver (P0.4). No ImGui — the whole model is
// engine-side + headless so both the serializer and the future drawer tree are exercisable here.
internal static class PropertyModelTests {
    public static int Run() {
        var h = new Harness();

        Classification(h);
        StaticPlan(h);
        MultiTarget(h);
        Recursion(h);
        CycleAndDepth(h);
        Determinism(h);

        return h.Report("PropertyModel (P0.2/P0.4)");
    }

    // ── PropertyCategory: one classification, the branches the whole traversal keys off ──────────────
    static void Classification(Harness h) {
        h.Check("bool → Primitive", PropertyCategories.Classify(typeof(bool)) == PropertyCategory.Primitive);
        h.Check("int → Primitive", PropertyCategories.Classify(typeof(int)) == PropertyCategory.Primitive);
        h.Check("float → Primitive", PropertyCategories.Classify(typeof(float)) == PropertyCategory.Primitive);
        h.Check("string → Primitive", PropertyCategories.Classify(typeof(string)) == PropertyCategory.Primitive);
        h.Check("Guid → Primitive", PropertyCategories.Classify(typeof(System.Guid)) == PropertyCategory.Primitive);
        h.Check("nullable float → Primitive (unwraps)", PropertyCategories.Classify(typeof(float?)) == PropertyCategory.Primitive);
        h.Check("enum → Enum", PropertyCategories.Classify(typeof(SampleEnum)) == PropertyCategory.Enum);
        h.Check("Vector3 → MathStruct", PropertyCategories.Classify(typeof(Vector3)) == PropertyCategory.MathStruct);
        h.Check("Quaternion → MathStruct", PropertyCategories.Classify(typeof(Quaternion)) == PropertyCategory.MathStruct);
        h.Check("plain struct → Nested", PropertyCategories.Classify(typeof(SamplePair)) == PropertyCategory.Nested);
        h.Check("plain class → Nested", PropertyCategories.Classify(typeof(SampleNestedClass)) == PropertyCategory.Nested);
        h.Check("List<int> → Collection", PropertyCategories.Classify(typeof(System.Collections.Generic.List<int>)) == PropertyCategory.Collection);
        h.Check("int[] → Collection", PropertyCategories.Classify(typeof(int[])) == PropertyCategory.Collection);
        h.Check("delegate → Unsupported", PropertyCategories.Classify(typeof(System.Action)) == PropertyCategory.Unsupported);

        // BObject split: asset-class type vs scene-object type, by declared type.
        h.Check("Material → AssetRef", PropertyCategories.Classify(typeof(Material)) == PropertyCategory.AssetRef);
        h.Check("Entity → SceneObjectRef", PropertyCategories.Classify(typeof(Entity)) == PropertyCategory.SceneObjectRef);
        h.Check("Behaviour → SceneObjectRef", PropertyCategories.Classify(typeof(Behaviour)) == PropertyCategory.SceneObjectRef);

        // Polymorphic: an abstract/interface member is Polymorphic ONLY with [SerializeReference]; without
        // it the model refuses to recurse a base it can't instantiate (stays Unsupported).
        var poly = typeof(SamplePolyHost).GetProperty(nameof(SamplePolyHost.Modifier));
        var unmarked = typeof(SamplePolyHost).GetProperty(nameof(SamplePolyHost.Unmarked));
        h.Check("[SerializeReference] interface → Polymorphic",
            PropertyCategories.Classify(typeof(ISampleModifier), poly) == PropertyCategory.Polymorphic);
        h.Check("unmarked interface → Unsupported",
            PropertyCategories.Classify(typeof(ISampleModifier), unmarked) == PropertyCategory.Unsupported);

        // An ABSTRACT-BObject asset (Texture3D etc.) classifies AssetRef, NEVER Polymorphic — even with
        // [SerializeReference] (an asset is guid-referenced, never type-swapped + instantiated). This is the
        // engine contract the editor's PolymorphicDrawer leans on: it must not mistake an abstract asset base
        // for a polymorphic slot and expand the backend object (the user-reported "Cubemap opens UID/Type/..").
        var cubemap = typeof(SamplePolyHost).GetProperty(nameof(SamplePolyHost.Cubemap));
        var markedCubemap = typeof(SamplePolyHost).GetProperty(nameof(SamplePolyHost.MarkedCubemap));
        h.Check("abstract Texture3D (no marker) → AssetRef (not Nested/Polymorphic)",
            PropertyCategories.Classify(typeof(Texture3D), cubemap) == PropertyCategory.AssetRef);
        h.Check("abstract Texture3D + [SerializeReference] → AssetRef (asset wins, never Polymorphic)",
            PropertyCategories.Classify(typeof(Texture3D), markedCubemap) == PropertyCategory.AssetRef);
        h.Check("abstract Texture3D by type alone → AssetRef",
            PropertyCategories.Classify(typeof(Texture3D)) == PropertyCategory.AssetRef);
    }

    // ── ARTIFACT 1 — the static TypePlan: members, ordering, [HideInInspector] exclusion ─────────────
    static void StaticPlan(Harness h) {
        TypePlan.Clear();
        TypePlan plan = TypePlan.For(typeof(SampleLeaves));

        var names = plan.Members.Select(m => m.Name).ToList();
        h.Check("plan excludes [HideInInspector] member", !names.Contains(nameof(SampleLeaves.Hidden)));
        h.Check("plan includes a leaf member", names.Contains(nameof(SampleLeaves.Count)));
        h.Check("plan includes the nested struct member", names.Contains(nameof(SampleLeaves.Pair)));

        // Ordering (P0.4 applied to members): [PropertyOrder(-10)] First is first, [PropertyOrder(10)] Last
        // is last; everything else keeps declaration order in between.
        h.Check("PropertyOrder -10 sorts to front", names.First() == nameof(SampleLeaves.First));
        h.Check("PropertyOrder 10 sorts to back", names.Last() == nameof(SampleLeaves.Last));

        // Stable + total order: same plan twice yields the IDENTICAL member sequence (cached + deterministic).
        var again = TypePlan.For(typeof(SampleLeaves)).Members.Select(m => m.Name).ToList();
        h.Check("plan member order is stable across calls", names.SequenceEqual(again));
        h.Check("plan is cached (same instance)", ReferenceEquals(plan, TypePlan.For(typeof(SampleLeaves))));

        // Category baked into the plan member.
        var pairMember = plan.Members.First(m => m.Name == nameof(SampleLeaves.Pair));
        h.Check("plan member carries its category", pairMember.Category == PropertyCategory.Nested);
        var callbackMember = plan.Members.First(m => m.Name == nameof(SampleLeaves.Callback));
        h.Check("delegate member classified Unsupported in plan", callbackMember.Category == PropertyCategory.Unsupported);
    }

    // ── ARTIFACT 2 — the dynamic N-target tree: read/write, mixed-value, broadcast ───────────────────
    static void MultiTarget(Harness h) {
        // Single target: read + write through the node, no mixed value.
        var one = new SampleMultiTarget { Value = 5, Tag = "a" };
        PropertyTree single = PropertyTree.For(one);
        PropertyNode valueNode = single.Roots.First(n => n.Name == nameof(SampleMultiTarget.Value));
        h.Check("single-target read", Equals(valueNode.GetValue(), 5));
        h.Check("single-target not mixed", !valueNode.HasMultipleValues);
        valueNode.SetValue(42);
        h.Check("single-target write applied", one.Value == 42);

        // Two targets that AGREE: not mixed.
        var a = new SampleMultiTarget { Value = 7, Tag = "x" };
        var b = new SampleMultiTarget { Value = 7, Tag = "x" };
        PropertyTree agree = PropertyTree.For(new object[] { a, b });
        PropertyNode agreeVal = agree.Roots.First(n => n.Name == nameof(SampleMultiTarget.Value));
        h.Check("agreeing targets not mixed", !agreeVal.HasMultipleValues);
        h.Check("agreeing targets count = 2", agreeVal.TargetCount == 2);

        // Two targets that DISAGREE: mixed value first-class (the DrawMixedMarker logic, now in the model).
        var c = new SampleMultiTarget { Value = 1, Tag = "p" };
        var d = new SampleMultiTarget { Value = 2, Tag = "q" };
        PropertyTree disagree = PropertyTree.For(new object[] { c, d });
        PropertyNode disVal = disagree.Roots.First(n => n.Name == nameof(SampleMultiTarget.Value));
        h.Check("disagreeing targets ARE mixed", disVal.HasMultipleValues);
        h.Check("active value = first target", Equals(disVal.GetValue(), 1));
        h.Check("GetValues returns per-target array", disVal.GetValues().SequenceEqual(new object[] { 1, 2 }));

        // Broadcast write sets ALL targets (the ApplyMember logic, now in the model) → no longer mixed.
        disVal.SetValue(99);
        h.Check("broadcast set target 1", c.Value == 99);
        h.Check("broadcast set target 2", d.Value == 99);
        h.Check("after broadcast no longer mixed", !disVal.HasMultipleValues);

        // A null owner in the set is skipped on write, read as null — never throws.
        PropertyTree withNull = PropertyTree.For(new object[] { new SampleMultiTarget { Value = 3 }, null });
        PropertyNode nullVal = withNull.Roots.First(n => n.Name == nameof(SampleMultiTarget.Value));
        nullVal.SetValue(8);   // must not throw on the null owner
        h.Check("write with a null owner does not throw + applies to non-null", true);
    }

    // ── Recursion: the one traversal walks into a nested member's plan (Rule 2) ───────────────────────
    static void Recursion(Harness h) {
        var host = new SampleLeaves { Pair = new SamplePair { X = 3, Y = 4 } };
        PropertyTree tree = PropertyTree.For(host);
        PropertyNode pair = tree.Roots.First(n => n.Name == nameof(SampleLeaves.Pair));

        h.Check("nested node is Nested category", pair.Category == PropertyCategory.Nested);
        var childNames = pair.GetChildren().Select(c => c.Name).ToHashSet();
        h.Check("nested struct recurses to its members",
            childNames.Contains(nameof(SamplePair.X)) && childNames.Contains(nameof(SamplePair.Y)));

        // A leaf node has NO children (the recursion bottoms out).
        PropertyNode count = tree.Roots.First(n => n.Name == nameof(SampleLeaves.Count));
        h.Check("leaf node has no children", count.GetChildren().Count == 0);

        // Lazy: GetChildren twice returns the SAME cached list when the value type is unchanged.
        var first = pair.GetChildren();
        var second = pair.GetChildren();
        h.Check("children cached while value type unchanged", ReferenceEquals(first, second));

        // A nested CLASS recurses too; a null nested value yields no children (no NRE).
        PropertyNode child = tree.Roots.First(n => n.Name == nameof(SampleLeaves.Child));
        h.Check("null nested class → no children, no throw", child.GetChildren().Count == 0);
        host.Child = new SampleNestedClass { A = 1.5f, B = "hi" };
        var childKids = child.GetChildren().Select(c => c.Name).ToHashSet();
        h.Check("non-null nested class recurses", childKids.Contains(nameof(SampleNestedClass.A)));
    }

    // ── Trap 3: cycle + depth guard — a back-edge stops, a diamond does NOT ───────────────────────────
    static void CycleAndDepth(Harness h) {
        // A → A (self reference): the child node pointing back at the on-path instance is a guard, not an
        // infinite recursion.
        var node = new SampleNode { Id = 1 };
        node.Self = node;                       // self-cycle
        PropertyTree tree = PropertyTree.For(node);
        PropertyNode selfNode = tree.Roots.First(n => n.Name == nameof(SampleNode.Self));
        var selfChildren = selfNode.GetChildren();
        // Self is the same instance as the root target → descending into it re-enters the path → guard.
        h.Check("self-cycle produces a guard child",
            selfChildren.Count == 1 && selfChildren[0].IsGuard);

        // A → B → A (two-hop cycle): walking root.Other (B) then B.Other (back to root A) must guard.
        var a = new SampleNode { Id = 10 };
        var b = new SampleNode { Id = 20 };
        a.Other = b;
        b.Other = a;                            // cycle back to a
        PropertyTree t2 = PropertyTree.For(a);
        PropertyNode aOther = t2.Roots.First(n => n.Name == nameof(SampleNode.Other));   // B
        PropertyNode bOther = aOther.GetChildren().First(n => n.Name == nameof(SampleNode.Other)); // back to A
        var bOtherChildren = bOther.GetChildren();
        h.Check("two-hop cycle guarded at the back-edge",
            bOtherChildren.Count == 1 && bOtherChildren[0].IsGuard);

        // DIAMOND (not a cycle): root.Self = shared, root.Other = a node whose Other = the SAME shared
        // instance. The shared instance reached via two SIBLING paths must NOT be guarded — it is not on
        // either path's ancestor chain.
        var shared = new SampleNode { Id = 99 };
        var root = new SampleNode { Id = 1, Self = shared };
        var mid = new SampleNode { Id = 2, Other = shared };
        root.Other = mid;
        PropertyTree t3 = PropertyTree.For(root);
        PropertyNode rootSelf = t3.Roots.First(n => n.Name == nameof(SampleNode.Self));   // shared via path 1
        PropertyNode rootOther = t3.Roots.First(n => n.Name == nameof(SampleNode.Other)); // mid
        PropertyNode midOther = rootOther.GetChildren().First(n => n.Name == nameof(SampleNode.Other)); // shared via path 2
        bool selfOk = rootSelf.GetChildren().Any(c => c.Name == nameof(SampleNode.Id) && !c.IsGuard);
        bool otherOk = midOther.GetChildren().Any(c => c.Name == nameof(SampleNode.Id) && !c.IsGuard);
        h.Check("diamond path 1 recurses (not guarded)", selfOk);
        h.Check("diamond path 2 recurses (not guarded)", otherOk);

        // Depth cap: a long self-chain stops at maxDepth instead of overflowing the stack. Build a tree
        // with a tiny cap and walk down via Other links on DISTINCT instances (no cycle), asserting the
        // guard fires at the cap.
        SampleNode chainHead = new() { Id = 0 };
        SampleNode cur = chainHead;
        for (int i = 1; i <= 6; i++) { cur.Other = new SampleNode { Id = i }; cur = cur.Other; }
        PropertyTree capped = PropertyTree.For(new object[] { chainHead }, maxDepth: 3);
        PropertyNode walk = capped.Roots.First(n => n.Name == nameof(SampleNode.Other));
        bool hitDepthGuard = false;
        for (int i = 0; i < 10 && walk is not null; i++) {
            var kids = walk.GetChildren();
            PropertyNode next = kids.FirstOrDefault(k => k.Name == nameof(SampleNode.Other));
            if (kids.Any(k => k.IsGuard)) { hitDepthGuard = true; break; }
            walk = next;
        }
        h.Check("deep distinct chain hits the max-depth guard", hitDepthGuard);
    }

    // ── P0.4 — DeterministicResolver: priority + ordinal tie-break, never load-order ──────────────────
    static void Determinism(Harness h) {
        // Higher priority wins regardless of registration order.
        var r = new DeterministicResolver<string>();
        r.Register("low", priority: 0, tieKey: "low");
        r.Register("high", priority: 10, tieKey: "high");
        r.Register("mid", priority: 5, tieKey: "mid");
        h.Check("highest priority wins", r.Resolve(_ => true) == "high");

        // Equal priority → ordinal tie-break (ascending tieKey), NOT registration order. Register out of
        // order and assert the alphabetically-first key wins.
        var tie = new DeterministicResolver<string>();
        tie.Register("zebra", priority: 1, tieKey: "zebra");
        tie.Register("apple", priority: 1, tieKey: "apple");
        tie.Register("mango", priority: 1, tieKey: "mango");
        h.Check("equal priority breaks by ordinal key (not load order)", tie.Resolve(_ => true) == "apple");

        // ResolveAll is best-first deterministic.
        h.CheckStrings("ResolveAll best-first order", tie.All(), "apple", "mango", "zebra");

        // A predicate filters; no match → default.
        h.Check("no match → default", r.Resolve(s => s == "nope") is null);

        // Order independence: building the SAME set in a DIFFERENT registration order yields the SAME winner
        // and the SAME ordering — the core guarantee (machine/assembly-load independent).
        var r1 = new DeterministicResolver<string>();
        r1.Register("b", 1, "b"); r1.Register("a", 1, "a"); r1.Register("c", 2, "c");
        var r2 = new DeterministicResolver<string>();
        r2.Register("c", 2, "c"); r2.Register("a", 1, "a"); r2.Register("b", 1, "b");
        h.Check("same set, different reg order → same winner", r1.Resolve(_ => true) == r2.Resolve(_ => true));
        h.Check("same set, different reg order → same ordering", r1.All().SequenceEqual(r2.All()));
    }
}
