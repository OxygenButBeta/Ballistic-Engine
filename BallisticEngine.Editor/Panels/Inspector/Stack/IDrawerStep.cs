using System;

namespace BallisticEngine.Editor.Inspector;

// editor-rework B0 (Rule 1.5) — the RUNTIME half of the Odin-style drawer stack: a composable step that
// WRAPS the next step via CallNext(). This is the structural fix the flat decorator chain could not do — the
// old IPropertyDecorator exposed only FIXED hooks (Visible/BeforeRow/Enabled) run in sequence and could not
// NEST, so [HideIf] returning false and [ReadOnly]'s disable were coordinated by LIST ORDER, not composition.
// Here each step decides whether/how to call the next:
//   - a Visibility step returns early (CallNext NOT invoked) when hidden → the whole subtree is skipped;
//   - an Enable step does BeginDisabled() / CallNext() / EndDisabled();
//   - a Chrome step emits a header then CallNext();
//   - the Terminal step draws the value widget (or recurses) and does NOT call next (it's the leaf).
// Steps cannot break each other because each only WRAPS — and the ORDER is the engine's deterministic
// DrawerStackResolver plan (priority/stage, never load order), shared by the component AND volume paths so
// they can't drift (Conditions.cs's component-vs-volume divergence is killed: both build ONE stack).
public interface IDrawerStep {
    // The engine descriptor key this runtime step implements — links a concrete step to its resolved slot in
    // the deterministic stack so the editor binds steps to the resolver's order, not its own.
    string Key { get; }

    // Draw this step. `next` runs the rest of the stack (the inner steps + terminal). Return true if the
    // value was edited this frame (propagate `next`'s result unless this step swallows it). A step that hides
    // the subtree returns false WITHOUT calling next.
    bool Draw(IProperty property, IInspectorGui gui, Func<bool> next);
}
