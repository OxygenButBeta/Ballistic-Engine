namespace BallisticEngine.Editor;

// Shared selection state across panels. Selecting an entity clears the asset selection and
// vice versa — the Inspector shows whichever is current.
internal sealed class EditorState {
    public Entity Selected { get; set; }
    public string SelectedAssetPath { get; private set; }
    public Guid SelectedAssetGuid { get; private set; }

    public bool HasAssetSelection => SelectedAssetPath is not null;

    public void Select(Entity entity) {
        Selected = entity;
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
    }

    public void SelectAsset(string assetPath, Guid guid) {
        SelectedAssetPath = assetPath;
        SelectedAssetGuid = guid;
        Selected = null;
    }

    public void ClearAssetSelection() {
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
    }

    public void ClearIfDestroyed(Scene scene) {
        if (Selected is not null && !scene.Entities.Contains(Selected))
            Selected = null;
    }
}
