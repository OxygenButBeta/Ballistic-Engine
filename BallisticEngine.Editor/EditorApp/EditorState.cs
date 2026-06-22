
namespace BallisticEngine.Editor;

internal sealed class EditorState {
    Entity selected;

    public Vector3 SceneSpawnPoint { get; set; } = Vector3.Zero;

    public Entity Selected {
        get => selected;
        set {
            selected = value;
            SelectedEntities.Clear();
            if (value is not null)
                SelectedEntities.Add(value);
        }
    }

    public List<Entity> SelectedEntities { get; } = new();

    public bool IsEntitySelected(Entity e) => SelectedEntities.Contains(e);

    public void ToggleEntity(Entity entity) {
        if (SelectedEntities.Remove(entity)) {
            selected = SelectedEntities.Count > 0 ? SelectedEntities[^1] : null;
        }
        else {
            SelectedEntities.Add(entity);
            selected = entity;
        }
        SelectedSceneBehaviour = null;
        ClearAssetSelection();
    }

    public void SelectEntities(IEnumerable<Entity> range, Entity active) {
        SelectedEntities.Clear();
        SelectedEntities.AddRange(range);
        if (!SelectedEntities.Contains(active))
            SelectedEntities.Add(active);
        selected = active;
        SelectedSceneBehaviour = null;
        ClearAssetSelection();
    }

    public SceneBehaviour SelectedSceneBehaviour { get; private set; }
    public string SelectedAssetPath { get; private set; }
    public Guid SelectedAssetGuid { get; private set; }

    public bool ViewportDirty { get; private set; }

    public void MarkViewportDirty() => ViewportDirty = true;

    public bool ConsumeViewportDirty() {
        if (!ViewportDirty)
            return false;
        ViewportDirty = false;
        return true;
    }

    public string RevealAssetRequest { get; private set; }

    public void RequestRevealAsset(string path) => RevealAssetRequest = path;

    public string ConsumeRevealAsset() {
        string p = RevealAssetRequest;
        RevealAssetRequest = null;
        return p;
    }

    public List<(string Path, Guid Guid)> SelectedAssets { get; } = new();

    public bool HasAssetSelection => SelectedAssetPath is not null;

    public void Select(Entity entity) {
        Selected = entity;
        SelectedSceneBehaviour = null;
        ClearAssetSelection();
    }

    public void SelectSceneBehaviour(SceneBehaviour behaviour) {
        SelectedSceneBehaviour = behaviour;
        Selected = null;
        ClearAssetSelection();
    }

    public void SelectAsset(string assetPath, Guid guid) {
        SetActiveAsset(assetPath, guid);
        SelectedAssets.Clear();
        SelectedAssets.Add((assetPath, guid));
    }

    public void ToggleAsset(string assetPath, Guid guid) {
        var index = SelectedAssets.FindIndex(a => a.Guid == guid);
        if (index < 0) {
            SetActiveAsset(assetPath, guid);
            SelectedAssets.Add((assetPath, guid));
            return;
        }

        SelectedAssets.RemoveAt(index);
        if (SelectedAssets.Count == 0) {
            ClearAssetSelection();
        }
        else if (SelectedAssetGuid == guid) {
            (string path, Guid g) = SelectedAssets[^1];
            SetActiveAsset(path, g);
        }
    }

    public void SelectAssets(IEnumerable<(string Path, Guid Guid)> items, (string Path, Guid Guid) active) {
        SelectedAssets.Clear();
        SelectedAssets.AddRange(items);
        if (!SelectedAssets.Any(a => a.Guid == active.Guid))
            SelectedAssets.Add(active);
        SetActiveAsset(active.Path, active.Guid);
    }

    public bool IsAssetSelected(Guid guid) {
        foreach ((_, Guid g) in SelectedAssets)
            if (g == guid)
                return true;
        return false;
    }

    public void ClearAssetSelection() {
        SelectedAssetPath = null;
        SelectedAssetGuid = Guid.Empty;
        SelectedAssets.Clear();
    }

    void SetActiveAsset(string assetPath, Guid guid) {
        SelectedAssetPath = assetPath;
        SelectedAssetGuid = guid;
        Selected = null;
        SelectedSceneBehaviour = null;
    }

    public void ClearIfDestroyed(Scene scene) {
        SelectedEntities.RemoveAll(e => !scene.Entities.Contains(e));
        if (selected is not null && !scene.Entities.Contains(selected))
            selected = SelectedEntities.Count > 0 ? SelectedEntities[^1] : null;
        if (SelectedSceneBehaviour is not null && !scene.SceneBehaviours.Contains(SelectedSceneBehaviour))
            SelectedSceneBehaviour = null;
    }
}
