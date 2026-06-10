namespace BallisticEngine.Editor;

// Shared mutable state across panels (current selection, etc.).
internal sealed class EditorState {
    public Entity Selected { get; set; }

    public void Select(Entity entity) => Selected = entity;

    public void ClearIfDestroyed(Scene scene) {
        if (Selected is not null && !scene.Entities.Contains(Selected))
            Selected = null;
    }
}
