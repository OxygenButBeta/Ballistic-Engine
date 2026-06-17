namespace BallisticEngine;

// editor-rework B0 (Phase B, Rule 1.5): the ordered STAGES a composable drawer stack passes through for a
// member. The Odin model replaces the old FLAT decorator list (Visible/BeforeRow/Enabled hooks run in a
// fixed sequence) with a STACK where each step WRAPS the next via CallNext(). The stack order is a total,
// deterministic function of (Stage, Priority, key) — never registration / assembly-load order (P0.4).
//
// Stages run OUTER→INNER (the first listed wraps everything inside it). A step at an earlier stage can
// short-circuit the whole subtree (Visibility returns early when hidden) or change how the inner steps draw
// (Enable wraps them in BeginDisabled, Chrome emits a header above them). The Terminal stage is the leaf:
// exactly one terminal draws the value widget (or recurses into a nested type's members). This enum is the
// ONLY place stage order is declared — both the engine resolver and the editor's runtime stack read it, so
// they cannot drift (the Conditions.cs component-vs-volume drift this chunk kills).
public enum DrawerStage {
    // Outermost. [ShowIf]/[HideIf] — returns early (the member's whole row + any nested subtree is skipped)
    // when the condition hides it. Must wrap everything so a hidden member costs nothing inside.
    Visibility = 0,

    // Chrome emitted ABOVE the row before the value draws: [Header] separator, [Space] gap. Wraps the
    // enable+terminal so the header still shows even when the value is disabled.
    Chrome = 10,

    // [ReadOnly] / [EnableIf] / [DisableIf] — wraps the terminal in BeginDisabled()/EndDisabled() so the
    // label stays live but the widget is greyed. Inner of Chrome (header is never disabled), outer of Terminal.
    Enable = 20,

    // The leaf: the type drawer (bool/float/enum/Vector3/...) OR a foldout that recurses into a nested type's
    // members (Rule 2). Exactly one terminal per stack; it calls no inner step (CallNext bottoms out here).
    Terminal = 100,
}
