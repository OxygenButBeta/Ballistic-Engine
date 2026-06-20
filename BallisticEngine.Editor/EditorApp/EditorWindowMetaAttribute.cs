namespace BallisticEngine.Editor;

// Marks an EditorWindow subclass as a user-discoverable editor window (Unity's [EditorWindow]/menu model).
// A window carrying this is auto-discovered by TypeCache, instantiated once (it MUST have a public
// parameterless constructor), given a Window-menu entry at MenuPath, and drawn through WindowShell — the
// author writes ONLY OnGui(IEditorGui) and never touches ImGui or the editor shell.
//
// This is the extension point for GAME developers: their editor windows live in a separate
// GameEditorScripts assembly (Assets/Editor/, editor-only, never in the player build), subclass the public
// EditorWindow, mark themselves with this attribute, and appear in the editor with zero wiring. Built-in
// engine panels that need constructor arguments (Inspector/Build/...) are registered explicitly instead and
// do NOT carry this attribute.
//
// The attribute lives in the EDITOR assembly (next to EditorWindow) — both the editor and any
// GameEditorScripts assembly reference it, and TypeCache scans across all loaded assemblies, so a
// game-script window is found exactly like a built-in one.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EditorWindowMetaAttribute : Attribute {
    public EditorWindowMetaAttribute(string title, string menuPath = null, int order = 100) {
        Title = title;
        MenuPath = menuPath ?? $"Window/{title}";
        Order = order;
    }

    // Display title (window title bar + the Window-menu leaf if MenuPath ends with it).
    public string Title { get; }

    // Full menu path, e.g. "Window/My Tool" or "Tools/Custom/Thing". Defaults to "Window/<Title>".
    public string MenuPath { get; }

    // Sort order among siblings under the same menu (ascending; same rule as [MenuItem]).
    public int Order { get; }

    // Lucide icon glyph for the title bar (optional). Set via the named property: [EditorWindowMeta("X", Icon = EditorIcons.Wrench)].
    public string Icon { get; set; }

    // Default floating size (width/height in DPI-independent units; scaled by UiScale at draw time).
    public float Width { get; set; } = 420f;
    public float Height { get; set; } = 540f;
}
