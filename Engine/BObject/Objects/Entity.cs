namespace BallisticEngine;

public class Entity : BObject {
    public Transform transform { get; internal set; }
    public List<Behaviour> Behaviours { get; internal set; }

    public bool IsActive { get; private set; }

    public string Tag { get; set; } = TagManager.Untagged;

    public int Layer { get; set; }

    public Guid PrefabSource { get; set; } = Guid.Empty;

    public bool IsPrefabInstance => PrefabSource != Guid.Empty;

    public bool CompareTag(string tag) => Tag == tag;

    public bool IsDestroyed { get; internal set; }

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

    public Behaviour AddComponent(Type componentType) {
        if (!typeof(Behaviour).IsAssignableFrom(componentType))
            throw new ArgumentException($"{componentType.Name} is not a Behaviour.", nameof(componentType));

        var component = (Behaviour)Activator.CreateInstance(componentType);
        Attach(component);
        return component;
    }

    void Attach(Behaviour component) {
        component.AttachToEntity(this);
        Behaviours.Add(component);

        try { component.OnAttach(); }
        catch (Exception e) { ScriptGuard.Report(component, "OnAttach", e); }

        if (SceneManager.IsPlaying && !SceneManager.SuppressPlayLifecycle && component.IsActive)
            component.FireEnable();
    }

    public void RemoveComponent(Behaviour component) {
        if (component is null || !Behaviours.Remove(component))
            return;

        component.IsDetached = true;
        if (SceneManager.IsPlaying && component.IsActive)
            component.FireDisable();
        try { component.OnDetach(); }
        catch (Exception e) { ScriptGuard.Report(component, "OnDetach", e); }
    }

    public T GetComponent<T>() where T : Behaviour {
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour is T t)
                return t;
        return null!;
    }

    public bool TryGetComponent<T>(out T component) where T : Behaviour {
        component = GetComponent<T>();
        return component is not null;
    }

    public List<T> GetComponents<T>() where T : class {
        var result = new List<T>();
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour is T t)
                result.Add(t);
        return result;
    }

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

    public IEnumerable<Entity> DirectChildren() {
        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(e.transform.Parent, transform))
                yield return e;
    }

    public void SetActive(bool isActive) {
        if (IsActive == isActive)
            return;

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
                behaviour.FireEnable();
            }
            else {
                behaviour.FireDisable();
            }
        }
    }

    static void CollectActiveStates(Entity root, List<(Behaviour, bool)> into) {
        foreach (Behaviour behaviour in root.Behaviours)
            into.Add((behaviour, behaviour.IsActive));

        foreach (Entity e in SceneManager.GetCurrentScene().Entities)
            if (ReferenceEquals(e.transform.Parent, root.transform))
                CollectActiveStates(e, into);
    }

    internal void FireBegin() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            behaviour.FireEnable();
        }
    }

    internal void FireEnd() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached || !behaviour.IsActive)
                continue;
            behaviour.FireDisable();
        }
    }

    internal void DetachAll() {
        foreach (Behaviour behaviour in Behaviours.ToArray()) {
            if (behaviour.IsDetached)
                continue;
            behaviour.IsDetached = true;
            try { behaviour.OnDetach(); }
            catch (Exception e) { ScriptGuard.Report(behaviour, "OnDetach", e); }
        }
    }

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
