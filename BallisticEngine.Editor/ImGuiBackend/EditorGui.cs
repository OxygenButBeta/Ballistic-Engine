namespace BallisticEngine.Editor;

// The process-wide IEditorGui handle. The editor has exactly ONE ImGuiEditorGui (it is stateless — the
// draw-list/input sub-adapters rebind per access), so a single shared instance is safe to reach from
// anywhere that draws inside an active ImGui window. EditorApplication sets this once at startup.
//
// This exists so the inspector's IInspectorGui adapters (ImGuiComponentGui / ImGuiVolumeGui) can route
// their widget calls through the seam WITHOUT threading an IEditorGui parameter through the entire
// inspector draw pipeline (DrawerStack -> decorators -> InspectorPanel -> VolumeProfileEditor ->
// AssetInspectors / ComponentPreviews). The adapters grab EditorGui.Shared; everything stays the seam.
//
// Use ONLY for code that always runs inside a frame the editor is drawing (inspector adapters, etc.).
// Window bodies get their gui from WindowShell directly and must not need this.
public static class EditorGui {
    public static IEditorGui Shared { get; internal set; }
}
