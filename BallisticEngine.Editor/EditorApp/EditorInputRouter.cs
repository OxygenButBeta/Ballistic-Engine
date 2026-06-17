using System;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace BallisticEngine.Editor;

// A4 (editor-rework): the editor's hotkeys as ONE declarative, priority-resolved, conflict-checkable table
// instead of scattered inline `if (ImGui.IsKeyPressed(K) && !ctrl && hovered)` checks across OnUpdate/BuildUI.
// This is the input-half of the shell, the way A2's pass list was the frame-loop half — and it rides the SAME
// engine-side substrate family: InputActionChain<Keys> (priority-resolve, headless-tested R-core) is the input
// analogue of OrderedPassList<T>, resolving a chord to at most one action per CONTEXT.
//
// CONTEXT is the load-bearing idea. Every shell hotkey belonged to one of three scopes, formerly enforced by an
// ad-hoc guard at each call site (Global = `CtrlDown && !WantTextInput`; SceneView = `sceneViewHovered &&
// !RightMouseDown`; the Esc/maximize case). Here each binding declares its EditorInputContext, the router builds
// the live context mask once per dispatch, and the substrate refuses to fire a SceneView binding while only
// Global is in scope — so a chord CANNOT leak between scene-view and game-view by construction, not by an inline
// `if (hovered)` someone can forget. The same property makes the binding set CONFLICT-CHECKABLE (gizmo R vs
// Ctrl+R rebuild, F vs Ctrl+Shift+F): InputActionChain.CheckConflicts is pure, so the harness asserts the real
// table is unambiguous.
//
// Editor-only (the binding BODIES call EditorApplication methods / ImGui); the harnessed contract lives in the
// engine substrate (InputActionChain + the conflict check), tested with a fake key probe. This is the same
// split A2 used: engine substrate harnessed, editor binding not.
//
// Chord probe = RAW OpenTK edge (EditorInput.KeyPressed + Ctrl/ShiftDown), NOT ImGui's io. The scene-view
// focus/clipboard keys were ALREADY migrated to raw input (so Ctrl/Shift can't read a frame stale relative to
// the key edge — the bug that made Ctrl+Shift+F fall through to plain-F); routing every binding through the
// same raw probe makes that uniform. The gizmo W/E/R keys moved from ImGui.IsKeyPressed to the same raw edge —
// behaviour-equivalent under the preserved SceneView (hovered) context gate.

// The scopes a hotkey can belong to. Bit flags so the router ORs the live ones into a single mask per dispatch.
// These mirror the EXACT gating the inline guards used, kept distinct so the migration is behaviour-identical:
//   Global ............ fires regardless of which panel is focused (undo/redo/save/rebuild). Live whenever the
//                       caller's typing/modifier gate passes.
//   SceneView ......... the Scene tab is the active view AND the user isn't typing/flying -- fires from ANY
//                       panel (Unity-style frame/clipboard: select in the Hierarchy, press F). Does NOT require
//                       the mouse to hover the viewport.
//   SceneViewHovered .. the stricter sub-scope: the mouse is actually over the Scene image (the gizmo W/E/R mode
//                       keys, which must not steal a key while the cursor is elsewhere). A frame with the cursor
//                       over the viewport is BOTH SceneView and SceneViewHovered (the host ORs both in).
//   GameView .......... reserved for play-mode game hotkeys (none routed yet -- the slot exists so a future game
//                       binding can't be parked in Global by default and leak into edit mode).
[Flags]
internal enum EditorInputContext {
    None            = 0,
    Global          = 1 << 0,
    SceneView       = 1 << 1,
    SceneViewHovered = 1 << 2,
    GameView        = 1 << 3,
}

// Stable Ids for the routed actions. The InputActionChain breaks priority ties by Id (ordinal), and the harness
// references these, so they are an explicit contract, not free-form strings sprinkled at call sites.
internal static class EditorActions {
    public const string Undo          = "edit.undo";
    public const string Redo          = "edit.redo";
    public const string Save          = "file.save";
    public const string RebuildScripts = "scripts.rebuild";
    public const string ExitMaximize  = "view.exitMaximize";
    public const string GizmoTranslate = "gizmo.translate";
    public const string GizmoRotate    = "gizmo.rotate";
    public const string GizmoScale     = "gizmo.scale";
    public const string FrameSelected  = "scene.frameSelected";
    public const string AlignToView    = "scene.alignToView";
    public const string CopyEntity      = "scene.copyEntity";
    public const string PasteEntity     = "scene.pasteEntity";
}

// The editor's router: owns the InputActionChain<Keys>, the raw-input chord probe, and the per-frame context
// mask. EditorApplication registers its actions once in the ctor (Define), then calls Dispatch from the two
// surviving dispatch moments (OnUpdate's global Ctrl pass + BuildUI's scene-view pass), passing the live
// context. The router never holds EditorApplication state beyond the EditorInput it probes.
internal sealed class EditorInputRouter {
    readonly InputActionChain<Keys> chain = new();
    readonly EditorInput input;

    // Set by the host each frame BEFORE dispatch so an action's enabled-gate (e.g. Save disabled while playing)
    // resolves against live state without the router reaching into EditorApplication. Null = no gate.
    public Func<string, bool> ActionEnabled;

    public EditorInputRouter(EditorInput input) =>
        this.input = input ?? throw new ArgumentNullException(nameof(input));

    // Register one binding. Priority disambiguates two active bindings (higher wins). The default priorities
    // below encode the existing precedence: a chord-specific Ctrl+ binding (Ctrl+R rebuild) outranks the bare-key
    // SceneView binding (R gizmo-scale) — though in practice the context mask already separates them, the
    // priority is the tie-break the conflict check verifies is never NEEDED within one context.
    public EditorInputRouter Bind(string id, KeyChord<Keys> chord, EditorInputContext context, Action invoke,
                                  int priority = 0) {
        chain.Add(id, chord, (int)context, priority, invoke);
        return this;
    }

    public void Build() => chain.Build();

    // Resolve + invoke the single winning action for the live context mask, using the raw-input chord probe.
    // Returns true if an action fired. The host calls this with exactly the contexts live at that dispatch
    // moment (Global from OnUpdate; Global|SceneView from the scene-view BuildUI pass), so a SceneView binding
    // can't fire from the global pass and vice-versa.
    public bool Dispatch(EditorInputContext liveContexts) =>
        chain.Dispatch((int)liveContexts, IsChordActive, GateFor);

    bool GateFor(InputAction<Keys> a) => ActionEnabled is null || ActionEnabled(a.Id);

    // The raw-OpenTK chord probe: the key's down-EDGE this frame AND the modifier state matches exactly. Reading
    // modifiers from the SAME source as the edge (EditorInput, not ImGui io) is what keeps Ctrl+Shift+F from
    // racing the F edge. Exact-match on modifiers means a bare-F binding does NOT fire while Ctrl is held (so
    // Ctrl+Shift+F never also triggers plain Frame), and Ctrl+R never triggers the bare-R gizmo binding.
    bool IsChordActive(KeyChord<Keys> chord) =>
        input.KeyPressed(chord.Key) &&
        input.CtrlDown == chord.Ctrl &&
        input.ShiftDown == chord.Shift;

    // Pure, input-independent: the registered table's conflicts (same chord+context+priority). The harness
    // asserts the editor's real table returns none; an editor diagnostic could surface any.
    public System.Collections.Generic.IReadOnlyList<InputConflict> Conflicts() => chain.CheckConflicts();
}
