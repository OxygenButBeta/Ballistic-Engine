namespace BallisticEngine.Editor;

internal static class InspectorUndo {
    public static Entity ScopeEntity { get; set; }

    public static bool Track(string label, bool changed) => EditorCommands.TrackEdit(label, ScopeEntity, changed);
}
