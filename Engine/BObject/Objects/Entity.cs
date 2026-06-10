namespace BallisticEngine;

public class Entity : BObject {
    public Transform transform { get; internal set; }
    public List<Behaviour> Behaviours { get; internal set; }
    public bool IsActive { get; private set; }

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
    // Scene.FireBegin() runs OnBegin/OnEnabled when play starts. In play mode, fire immediately.
    void Attach(Behaviour component) {
        component.AttachToEntity(this);
        Behaviours.Add(component);

        component.OnAttach(); // edit + play: registration/visibility

        if (SceneManager.IsPlaying) {
            component.OnBegin();
            if (component.IsActive)
                component.OnEnabled();
        }
    }

    public void RemoveComponent(Behaviour component) {
        if (component is null || !Behaviours.Remove(component))
            return;

        if (SceneManager.IsPlaying && component.IsActive)
            component.OnDisabled();
        component.OnDetach();
    }

    public T GetComponent<T>() where T : Behaviour {
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour is T t)
                return t;
        return null!;
    }

    public void SetActive(bool isActive) {
        IsActive = isActive;
        if (isActive) {
            foreach (Behaviour behaviour in Behaviours)
                behaviour.OnEnabled();
        }
        else {
            foreach (Behaviour behaviour in Behaviours)
                behaviour.OnDisabled();
        }
    }

    // Runs OnBegin then OnEnabled for every component (entering play mode).
    internal void FireBegin() {
        foreach (Behaviour behaviour in Behaviours) {
            behaviour.OnBegin();
            if (behaviour.IsActive)
                behaviour.OnEnabled();
        }
    }

    // Runs OnDisabled for every active component (leaving play mode).
    internal void FireEnd() {
        foreach (Behaviour behaviour in Behaviours)
            if (behaviour.IsActive)
                behaviour.OnDisabled();
    }

    // Runs OnDetach for every component (entity removed / scene cleared, edit or play).
    internal void DetachAll() {
        foreach (Behaviour behaviour in Behaviours)
            behaviour.OnDetach();
    }

    internal void Update(in float deltaTime) {
        foreach (Behaviour behaviour in Behaviours) {
            if (behaviour.IsActive)
                behaviour.Tick(in deltaTime);
        }
    }

    protected override void OnInstanceCreated() {
        SceneManager.GetCurrentScene().RegisterEntity(this);
    }
}
