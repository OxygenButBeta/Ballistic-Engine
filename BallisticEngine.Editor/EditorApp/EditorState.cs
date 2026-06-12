namespace BallisticEngine.Editor;

// Shared selection state across panels. Selecting an entity clears the asset selection and
// vice versa — the Inspector shows whichever is current.
// Assets support MULTI-selection (ctrl/shift-click in the browser): SelectedAssets holds every
// selected asset, while SelectedAssetPath/Guid stay the "active" one (shown in the Inspector).
internal sealed class EditorState {
    Entity selected;

    // The "active" entity — shown in the Inspector. Setting it directly (used by a few legacy sites)
    // collapses the multi-selection to just this entity, matching Unity's "click selects one".
    public Entity Selected {
        get => selected;
        set {
            selected = value;
            SelectedEntities.Clear();
            if (value is not null)
                SelectedEntities.Add(value);
        }
    }

    // Every selected entity, in selection order (Ctrl/Shift-click in the hierarchy). Always contains
    // Selected as its active member; a single click is a one-element list. Batch ops (delete/duplicate/
    // reparent/toggle) operate on this whole set.
    public List<Entity> SelectedEntities { get; } = new();

    public bool IsEntitySelected(Entity e) => SelectedEntities.Contains(e);

    // Ctrl-click: add/remove from the multi-selection; the toggled entity becomes active (or, if it
    // was the active one being removed, the most recent remaining entity does).
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

    // Shift-click range: replace the selection with `range`, activating `active`.
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

    // The editor renders the viewport on demand (see EditorApplication's renderScene guard), so a
    // panel edit that changes what the scene LOOKS like — toggling a light, disabling an entity,
    // editing a component value, adding/removing a component — must ask for a repaint or the stale
    // last frame stays on screen. Panels set this; the app consumes it (ConsumeViewportDirty) once
    // per frame and forces a few frames. Cheaper and less error-prone than threading a "changed"
    // return value back from every widget.
    public bool ViewportDirty { get; private set; }

    public void MarkViewportDirty() => ViewportDirty = true;

    public bool ConsumeViewportDirty() {
        if (!ViewportDirty)
            return false;
        ViewportDirty = false;
        return true;
    }

    // An asset path the inspector asked the asset browser to REVEAL (navigate to its folder + select
    // it), so clicking an asset reference jumps to it in the browser instead of swapping the inspector.
    // Set by the inspector, consumed once per frame by the asset browser (same pattern as ViewportDirty).
    public string RevealAssetRequest { get; private set; }

    public void RequestRevealAsset(string path) => RevealAssetRequest = path;

    public string ConsumeRevealAsset() {
        string p = RevealAssetRequest;
        RevealAssetRequest = null;
        return p;
    }

    // Every selected asset, in selection order. Non-empty iff an asset selection exists;
    // single-click selection is a one-element list.
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

    // Ctrl-click: add to / remove from the multi-selection. Removing the active asset promotes
    // the most recently selected remaining one; removing the last clears the selection.
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

    // Shift-click range select: replaces the selection with `items`, activating `active`.
    public void SelectAssets(IEnumerable<(string Path, Guid Guid)> items, (string Path, Guid Guid) active) {
        SelectedAssets.Clear();
        SelectedAssets.AddRange(items);
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
        // Prune any multi-selected entities that no longer exist (deleted, or replaced by an undo).
        SelectedEntities.RemoveAll(e => !scene.Entities.Contains(e));
        if (selected is not null && !scene.Entities.Contains(selected))
            selected = SelectedEntities.Count > 0 ? SelectedEntities[^1] : null;
        if (SelectedSceneBehaviour is not null && !scene.SceneBehaviours.Contains(SelectedSceneBehaviour))
            SelectedSceneBehaviour = null;
    }
}
