namespace BallisticEngine.Editor;

// Shared selection state across panels. Selecting an entity clears the asset selection and
// vice versa — the Inspector shows whichever is current.
internal sealed class EditorState {
    public Entity Selected { get; set; }
    public SceneBehaviour SelectedSceneBehaviour { get; private set; }
    public string SelectedAssetPath { get; private set; }
    public Guid SelectedAssetGuid { get; private set; }

    public bool HasAssetSelection => SelectedAssetPath is not null;

    public void Select(Entity entity) {
        Selected = entity;
        SelectedSceneBehaviour = null;
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
    }

    public void SelectSceneBehaviour(SceneBehaviour behaviour) {
        SelectedSceneBehaviour = behaviour;
        Selected = null;
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
    }

    public void SelectAsset(string assetPath, Guid guid) {
        SelectedAssetPath = assetPath;
        SelectedAssetGuid = guid;
        Selected = null;
        SelectedSceneBehaviour = null;
    }

    public void ClearAssetSelection() {
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
    }

    public void ClearIfDestroyed(Scene scene) {
        if (Selected is not null && !scene.Entities.Contains(Selected))
            Selected = null;
        if (SelectedSceneBehaviour is not null && !scene.SceneBehaviours.Contains(SelectedSceneBehaviour))
            SelectedSceneBehaviour = null;
    }
}
