namespace BallisticEngine;

public class Entity : BObject {
    public Transform transform { get; internal set; }
    public List<Behaviour> Behaviours { get; internal set; }
    public bool IsActive { get; private set; }

    public Entity(bool isActive) {
        transform = new Transform();
        transform.AttachToEntity(this);
        Behaviours = new List<Behaviour>(capacity: 4);
        IsActive = isActive;
    }

    public void AddComponent<T>() where T : Behaviour, new() {
        T component = new T();
        component.AttachToEntity(this);
        component.OnBegin();
            component.OnEnabled();

        Behaviours.Add(component);
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

    internal void Update(float deltaTime) {
        foreach (Behaviour behaviour in Behaviours) {
            behaviour.Tick(deltaTime);
        }
    }

    protected override void OnInstanceCreated() {
        Scene.Instance.RegisterEntity(this);
    }
}