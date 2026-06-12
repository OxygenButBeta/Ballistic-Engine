namespace BallisticEngine;

public class Entity : BObject {
    public Transform transform { get; internal set; }
    public List<Behaviour> Behaviours { get; internal set; }

    // This entity's OWN active flag (Unity's activeSelf) — toggled by SetActive / the editor.
    public bool IsActive { get; private set; }

    // Free-form tag (Unity's GameObject.tag) — gameplay grouping/identification. Defaults to
    // "Untagged". Serialized at the entity level (see SceneSerializer). Use CompareTag to test.
    public string Tag { get; set; } = TagManager.Untagged;

    // Physics/render layer index 0..31 (Unity's GameObject.layer). Drives the physics collision
    // matrix (LayerManager) and Raycast LayerMasks. Defaults to 0 ("Default"). Serialized at the
    // entity level. A collider/rigidbody reads this when it builds its body.
    public int Layer { get; set; }

    // GUID of the .prefab asset this entity is an INSTANCE of (Unity's prefab link), or Guid.Empty for
    // a plain scene entity. Set when a prefab is instantiated, or when the editor converts a live entity
    // into a prefab (drag-to-asset-browser). Serialized at the entity level so the link survives save/
    // load; the editor uses it to render the instance distinctly and to drive Apply/Revert against the
    // source asset. The root of an instantiated subtree carries it; descendants do not.
    public Guid PrefabSource { get; set; } = Guid.Empty;

    // True when this entity is the root of a prefab instance (has a live link to a .prefab asset).
    public bool IsPrefabInstance => PrefabSource != Guid.Empty;

    // Unity's CompareTag — exact string match, null/empty safe.
    public bool CompareTag(string tag) => Tag == tag;

    // Set by Scene.DestroyEntity BEFORE teardown: the entity may still sit in this frame's
    // dispatch snapshots, and a detached entity must not tick again (its components already
    // ran OnDisabled/OnDetach). Also lets game code test liveness after a destroy.
    public bool IsDestroyed { get; internal set; }

    // True only when this entity AND every ancestor are active (Unity's activeInHierarchy).
    // Walks the Transform.Parent chain, so disabling a parent stops the lights/volumes/renderers
    // on its children too. Components read this through Behaviour.IsActive, so the renderer needs
    // no changes. Cycles are impossible (parent links form a tree), so the walk always terminates.
    public bool IsActiveInHierarchy {
        get {
            for (Transform t = transform; t is not null; t = t.Parent)
                if (t.Entity is { IsActive: false })
                    return false;
            return true;
        }
    }

    public static Entity Instantiate(string name = "Entity", bool isActive = true) {
        Entity entity = new(name, isActive);
        entity.OnInstanceCreated();
        return entity;
    }

    private Entity(string name = "Entity", bool isActive = true) {
        Name = name;
        transform = new Transform();
        transform.AttachToEntity(this);
        Behaviours = new List<Behaviour>(capacity: 4);
        IsActive = isActive;
    }

    public T AddComponent<T>() where T : Behaviour, new() {
        T component = new();
        Attach(component);
        return component;
    }

    // Used by deserialization / the editor's Add Component menu (type known only at runtime).
    public Behaviour AddComponent(Type componentType) {
        if (!typeof(Behaviour).IsAssignableFrom(componentType))
            throw new ArgumentException($"{componentType.Name} is not a Behaviour.", nameof(componentType));

        var component = (Behaviour)Activator.CreateInstance(componentType);
        Attach(component);
        return component;
    }

    // In edit mode, components are attached and configured but their lifecycle does NOT run;
    // Scene.FireBegin() runs OnBegin/OnEnabled when play starts. In play mode, fire immediately —
    // UNLESS a scene deserialize is in flight (live script reload, runtime LoadScene): member
    // values are applied AFTER AddComponent, so OnBegin would observe defaults; the loader fires
    // FireBegin itself once the whole scene is rebuilt.
    void Attach(Behaviour component) {
        component.AttachToEntity(this);
        Behaviours.Add(component);

        try { component.OnAttach(); } // edit + play: registration/visibility
        catch (Exception e) { ScriptGuard.Report(component, "OnAttach", e); }

        // Play mode: activate it now IF it's active. A component added to a disabled entity (or added
        // disabled) defers its OnBegin until SetActive enables it — matching Unity (Awake/Start run on
        // first activation, not at AddComponent time on an inactive object).
        if (SceneManager.IsPlaying && !SceneManager.SuppressPlayLifecycle && component.IsActive)
            component.FireEnable();
    }

    public void RemoveComponent(Behaviour component) {
        if (component is null || !Behaviours.Remove(component))
            return;

        component.IsDetached = true; // in-flight dispatch snapshots skip it from here on
        if (SceneManager.IsPlaying && component.IsActive) {
            try { component.OnDisabled(); }
            catch (Exception e) { ScriptGuard.Report(component, "OnDisabled", e); }
        }
        try { component.OnDetach(); }
        catch (Exception e) { ScriptGuard.Report(component, "OnDetach", e); }
    }

    public T GetComponent<T>() where T : Behaviour {
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour is T t)
                return t;
        return null!;
    }

    // Pattern-friendly variant (Unity's TryGetComponent): returns false + null instead of a null
    // you have to test separately. Avoids the "GetComponent then null-check" two-liner.
    public bool TryGetComponent<T>(out T component) where T : Behaviour {
        component = GetComponent<T>();
        return component is not null;
    }

    // All components of type T on THIS entity (T may be an interface or base type — e.g.
    // GetComponents<Collider>() returns every collider). Allocates; for hot paths cache the result.
    public List<T> GetComponents<T>() where T : class {
        var result = new List<T>();
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour is T t)
                result.Add(t);
        return result;
    }

    // First component of type T on this entity OR any descendant (depth-first, self first).
    // includeInactive=false (default) skips components on inactive entities, matching Unity.
    public T GetComponentInChildren<T>(bool includeInactive = false) where T : class {
        if (includeInactive || IsActiveInHierarchy) {
            foreach (Behaviour behaviour in Behaviours)
                if (behaviour is T t)
                    return t;
        }

        foreach (Entity child in DirectChildren()) {
            T found = child.GetComponentInChildren<T>(includeInactive);
            if (found is not null)
                return found;
        }
        return null;
    }

    // First component of type T on this entity OR any ancestor (self first, walking up Parent).
    public T GetComponentInParent<T>(bool includeInactive = false) where T : class {
        for (Transform t = transform; t is not null; t = t.Parent) {
            Entity e = t.Entity;
            if (e is null || (!includeInactive && !e.IsActiveInHierarchy))
                continue;
            foreach (Behaviour behaviour in e.Behaviours)
                if (behaviour is T component)
                    return component;
        }
        return null;
    }

    // Direct children of this entity (entities whose Transform.Parent is this transform). Derived
    // from the scene's Parent links — the same source the editor hierarchy and SetActive walk use.
    public IEnumerable<Entity> DirectChildren() {
        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(e.transform.Parent, transform))
                yield return e;
    }

    public void SetActive(bool isActive) {
        if (IsActive == isActive)
            return;

        // Snapshot which components (here AND on descendants) are effectively active BEFORE the
        // flip, so we fire OnEnabled/OnDisabled only on the ones that actually transition. A child
        // that was already inactive (its own flag off, or a disabled component) must not get a
        // spurious callback when an ancestor toggles. Lifecycle only runs in play mode; in edit
        // mode SetActive just flips the flag and the next frame's gather reflects it.
        if (!SceneManager.IsPlaying) {
            IsActive = isActive;
            return;
        }

        List<(Behaviour b, bool wasActive)> affected = new();
        CollectActiveStates(this, affected);

        IsActive = isActive;

        foreach ((Behaviour behaviour, bool wasActive) in affected) {
            bool nowActive = behaviour.IsActive;
            if (nowActive == wasActive)
                continue;
            if (nowActive) {
                // Becoming active: FireEnable runs the DEFERRED OnBegin (once) before OnEnabled, so a
                // component first activated here — e.g. PlayerController on an entity that started
                // disabled — gets the OnBegin that spawns its camera. Later re-enables skip OnBegin.
                behaviour.FireEnable();
            }
            else {
                try { behaviour.OnDisabled(); }
                catch (Exception e) { ScriptGuard.Report(behaviour, "OnDisabled", e); }
            }
        }
    }

    // Records the current IsActive of every component on `root` and all its descendant entities,
    // so SetActive can diff against it after toggling the flag (see SetActive). Descendants are
    // found through the scene's Parent links — the same source the editor hierarchy uses.
    static void CollectActiveStates(Entity root, List<(Behaviour, bool)> into) {
        foreach (Behaviour behaviour in root.Behaviours)
            into.Add((behaviour, behaviour.IsActive));

        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(e.transform.Parent, root.transform))
                CollectActiveStates(e, into);
    }

    // The lifecycle dispatch loops below iterate a SNAPSHOT (ToArray) of Behaviours, not the live
    // list, so a component that adds or removes components on its OWN entity during a callback —
    // a legitimate Unity pattern, e.g. a controller that fabricates its capsule/rigidbody in
    // OnBegin — doesn't invalidate the enumerator. The mutation simply takes effect on the next
    // dispatch. Snapshotting (rather than a shared scratch buffer) also keeps this reentrancy-safe:
    // a callback that drives another entity's Update synchronously gets its own snapshot.

    // Entering play mode: activate every component that is active NOW (OnBegin once, then OnEnabled).
    // Components on an inactive entity, or that are themselves disabled, are intentionally left alone —
    // their OnBegin is DEFERRED to the first time they become active (Unity's Start semantics), fired
    // by SetActive. Without this deferral a controller that spawns its camera in OnBegin never spawned
    // it when the entity started disabled.
    internal void FireBegin() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            behaviour.FireEnable();
        }
    }

    // Runs OnDisabled for every active component (leaving play mode).
    internal void FireEnd() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            try { behaviour.OnDisabled(); }
            catch (Exception e) { ScriptGuard.Report(behaviour, "OnDisabled", e); }
        }
    }

    // Runs OnDetach for every component (entity removed / scene cleared, edit or play).
    internal void DetachAll() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached)
                continue;
            behaviour.IsDetached = true;
            try { behaviour.OnDetach(); }
            catch (Exception e) { ScriptGuard.Report(behaviour, "OnDetach", e); }
        }
    }

    // Index-based (not foreach) so a behaviour appending a component during Tick — Unity allows
    // this — doesn't throw; the new component simply isn't ticked until next frame. Allocation-free
    // on the per-frame path (unlike FireBegin's snapshot). A behaviour that REMOVES a component on
    // the same entity mid-loop could skip the next one; same-entity removal during Tick is rare and
    // RemoveComponent already runs its teardown, so this is an acceptable v1 trade-off.
    //
    // Exceptions are firewalled per component (ScriptGuard): a throwing Tick is logged with its
    // script stack trace and the rest of the frame proceeds; repeat offenders get auto-disabled.
    internal void Update(in float deltaTime) {
        for (int i = 0; i < Behaviours.Count; i++) {
            Behaviour behaviour = Behaviours[i];
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            try {
                behaviour.Tick(in deltaTime);
                if (behaviour.FaultStreak != 0 && behaviour.FaultCallback == "Tick")
                    behaviour.FaultStreak = 0;
            }
            catch (Exception e) {
                ScriptGuard.ReportRepeating(behaviour, "Tick", e);
            }
        }
    }

    internal void FixedUpdate(in float fixedDelta) {
        for (int i = 0; i < Behaviours.Count; i++) {
            Behaviour behaviour = Behaviours[i];
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            try {
                behaviour.FixedTick(in fixedDelta);
                if (behaviour.FaultStreak != 0 && behaviour.FaultCallback == "FixedTick")
                    behaviour.FaultStreak = 0;
            }
            catch (Exception e) {
                ScriptGuard.ReportRepeating(behaviour, "FixedTick", e);
            }
        }
    }

    protected override void OnInstanceCreated() {
        SceneManager.GetCurrentScene().RegisterEntity(this);
    }
}
