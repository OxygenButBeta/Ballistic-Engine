using System;
using System.Collections.Generic;
using System.Reflection;

namespace BallisticEngine.Editor.Inspector;

// editor-rework B0 (Rule 1.5) — the composable, deterministic drawer STACK that REPLACES DrawerPipeline's flat
// `[Conditional, ReadOnly, HeaderSpace]` decorator list + single terminal. This is the architectural core of
// Phase B: instead of running fixed hooks in sequence, it composes a chain of IDrawerStep where each WRAPS the
// next (CallNext), and the chain's ORDER is the engine's deterministic DrawerStackResolver plan (stage +
// priority + key, never assembly-load order — P0.4). The SAME stack serves the component inspector AND the
// volume profile editor (one IProperty + one IInspectorGui per host), so the two paths can no longer drift —
// the Conditions.cs direct-call divergence (component path called Conditions directly, volume path went
// through decorators) is eliminated: both now build ONE stack.
//
// STAGE PLACEMENT (byte-identical to the old DrawerPipeline.Draw bracket — verified against ImGuiVolumeGui):
//   Visibility  → evaluated FIRST; a hidden member draws NO row at all (return early).
//   Chrome      → runs BEFORE PushId/BeginRow (header/space emitted ABOVE the row).
//   PushId + BeginRow(property)                               (host draws the label / override checkbox).
//   Enable      → wraps ONLY the terminal (BeginDisabled/EndDisabled) — NOT BeginRow, so the volume override
//                 checkbox + label stay live exactly as the old pipeline kept them.
//   terminal    → the value widget (or recursion leaf).
//   EndRow + PopId.
// Each stage's steps compose via CallNext WITHIN that stage; the stages themselves are placed by these fixed
// semantics (the only place row-bracket order is encoded — it must match the host adapters' BeginRow chrome).
//
// Perf (§4): the deterministic per-member step order is resolved ONCE by the engine + cached (DrawerStackPlan,
// ARTIFACT-1, keyed by MemberInfo). This runtime maps resolved descriptor keys to concrete steps (a dict
// lookup) and folds CallNext closures — no reflection / attribute scan per frame. Hidden rows short-circuit
// before opening a row or folding inner closures.
public sealed class DrawerStack {
    readonly DrawerRegistry registry;
    readonly Dictionary<string, IDrawerStep> stepsByKey;
    readonly IDrawerStep terminal;

    DrawerStack(DrawerRegistry registry, IReadOnlyList<IDrawerStep> nonTerminalSteps, IDrawerStep terminal) {
        this.registry = registry;
        this.terminal = terminal;
        stepsByKey = new Dictionary<string, IDrawerStep>();
        foreach (IDrawerStep s in nonTerminalSteps)
            stepsByKey[s.Key] = s;
    }

    public DrawerRegistry Registry => registry;

    // The default (volume-path) stack: the FULL engine-known cross-cutting steps (visibility/chrome/enable) +
    // the type-drawer terminal leaf. Byte-identical to DrawerPipeline.CreateDefault's behaviour, as a
    // composable stack ordered by the engine resolver instead of a hand-written list.
    public static DrawerStack CreateDefault(DrawerRegistry registry = null) {
        DrawerRegistry reg = registry ?? DrawerRegistry.CreatePrimitive();
        return new DrawerStack(
            reg,
            new IDrawerStep[] { new VisibilityStep(), new HeaderSpaceStep(), new EnableStep() },
            new TypeDrawerTerminalStep(reg));
    }

    // The COMPONENT-path stack: the layout driver (InspectorPanel.DrawMemberList) already owns the
    // foldout/grid table + the [ShowIf]/[HideIf] skip + the out-of-grid [Header]/[Space] separators (which
    // must render OUTSIDE the grid table to stay byte-identical), so the component stack registers ONLY the
    // Enable step + terminal. The engine resolver still emits the visibility/chrome descriptors in the
    // deterministic order; this stack has no concrete step for them, so they're dropped — the same "engine
    // knows an attribute the editor doesn't draw HERE" path that keeps the order single-sourced.
    public static DrawerStack CreateComponent(DrawerRegistry registry = null) {
        DrawerRegistry reg = registry ?? DrawerRegistry.CreatePrimitive();
        return new DrawerStack(
            reg,
            new IDrawerStep[] { new EnableStep() },
            new TypeDrawerTerminalStep(reg));
    }

    // Draw one property through the resolved, composed stack. Returns true if the value was edited this frame
    // (false also when hidden).
    public bool Draw(IProperty property, IInspectorGui gui) {
        Staged staged = ResolveStaged(property);

        // Visibility (outermost): a hidden member draws NOTHING (no chrome, no row). Compose the visibility
        // steps around a probe that returns true if the subtree is visible.
        if (!RunVisibility(staged.Visibility, property, gui))
            return false;

        // Chrome BEFORE the row (header/space above it).
        foreach (IDrawerStep chrome in staged.Chrome)
            chrome.Draw(property, gui, AlwaysTrue);

        // The row, with Enable wrapping ONLY the terminal (so the host's BeginRow label/override checkbox stay
        // live — matches the old pipeline exactly).
        gui.PushId(property.Name);
        gui.BeginRow(property);
        try {
            Func<bool> drawTerminal = () => terminal.Draw(property, gui, NoNext);
            Func<bool> enabled = drawTerminal;
            for (int i = staged.Enable.Count - 1; i >= 0; i--) {
                IDrawerStep step = staged.Enable[i];
                Func<bool> inner = enabled;
                enabled = () => step.Draw(property, gui, inner);
            }
            return enabled();
        } finally {
            gui.EndRow();
            gui.PopId();
        }
    }

    // Compose the visibility steps; each may short-circuit (return false WITHOUT calling next) to hide. The
    // innermost "next" reports visible=true.
    static bool RunVisibility(IReadOnlyList<IDrawerStep> visSteps, IProperty p, IInspectorGui gui) {
        if (visSteps.Count == 0) return true;
        Func<bool> next = AlwaysTrue;
        for (int i = visSteps.Count - 1; i >= 0; i--) {
            IDrawerStep step = visSteps[i];
            Func<bool> inner = next;
            next = () => step.Draw(p, gui, inner);
        }
        return next();
    }

    static bool AlwaysTrue() => true;
    static bool NoNext() => false;

    // The concrete steps for this property's member, grouped by stage in the engine's deterministic order.
    readonly record struct Staged(
        IReadOnlyList<IDrawerStep> Visibility,
        IReadOnlyList<IDrawerStep> Chrome,
        IReadOnlyList<IDrawerStep> Enable);

    Staged ResolveStaged(IProperty property) {
        var vis = new List<IDrawerStep>();
        var chrome = new List<IDrawerStep>();
        var enable = new List<IDrawerStep>();

        MemberInfo member = StackMemberLookup.MemberOf(property);
        if (member is null)
            return new Staged(vis, chrome, enable);

        foreach (DrawerStackResolver.Descriptor d in DrawerStackPlan.For(member).Steps) {
            if (d.IsTerminal) continue;                                   // editor owns its own terminal
            if (!stepsByKey.TryGetValue(d.Key, out IDrawerStep step)) continue;
            switch (d.Stage) {
                case DrawerStage.Visibility: vis.Add(step); break;
                case DrawerStage.Chrome:     chrome.Add(step); break;
                case DrawerStage.Enable:     enable.Add(step); break;
            }
        }
        return new Staged(vis, chrome, enable);
    }
}

// Resolves the backing MemberInfo for an IProperty so the stack can key the resolved (cached) step order off
// the same MemberInfo for BOTH inspector hosts: the component path exposes its reflected member
// (MemberProperty.Member); the volume path exposes the parameter slot's backing field. Both carry the
// cross-cutting attributes the resolver reads.
internal static class StackMemberLookup {
    public static MemberInfo MemberOf(IProperty property) => property switch {
        MemberProperty mp => mp.Member,
        VolumeParamProperty vp => vp.Field,
        _ => null,
    };
}
