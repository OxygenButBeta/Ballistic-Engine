namespace BallisticEngine;

public class Scene : BObject
{
    // Scene is a container for all entities in the game world.
    // It manages the lifecycle of entities and their components.

    // Allocating a capacity of 200 entities for the scene. to ensure efficient memory usage.
    readonly List<Entity> entities = new(capacity: 200);

    // Scene-wide components (skybox, fog, ...). They live on the scene, not on entities,
    // and appear in the editor's "Scene" hierarchy.
    readonly List<SceneBehaviour> sceneBehaviours = new();

    public IReadOnlyList<Entity> Entities => entities;
    public IReadOnlyList<SceneBehaviour> SceneBehaviours => sceneBehaviours;

    public T AddSceneBehaviour<T>() where T : SceneBehaviour, new() =>
        (T)AddSceneBehaviour(typeof(T));

    public SceneBehaviour AddSceneBehaviour(Type type) {
        if (!typeof(SceneBehaviour).IsAssignableFrom(type))
            throw new ArgumentException($"{type.Name} is not a SceneBehaviour.", nameof(type));

        var behaviour = (SceneBehaviour)Activator.CreateInstance(type);
        sceneBehaviours.Add(behaviour);
        behaviour.OnAttach();
        return behaviour;
    }

    public void RemoveSceneBehaviour(SceneBehaviour behaviour) {
        if (behaviour is null || !sceneBehaviours.Remove(behaviour))
            return;
        behaviour.OnDetach();
    }

    public T GetSceneBehaviour<T>() where T : SceneBehaviour {
        foreach (SceneBehaviour behaviour in sceneBehaviours)
            if (behaviour is T t)
                return t;
        return null;
    }

    public void RegisterEntity(Entity entity)
    {
        entities.Add(entity);
    }

    public void RemoveEntity(Entity entity)
    {
        entities.Remove(entity);
    }

    // Create an entity that belongs to THIS scene (Entity.Instantiate registers into the
    // current scene; this lets the editor target a specific scene explicitly).
    public Entity CreateEntity(string name = "Entity")
    {
        Entity entity = Entity.Instantiate(name);
        if (!entities.Contains(entity))
            entities.Add(entity);
        return entity;
    }

    // Remove an entity and detach its components (so renderers leave their draw sets).
    public void DestroyEntity(Entity entity)
    {
        if (entity is null)
            return;

        if (SceneManager.IsPlaying)
            entity.FireEnd();
        entity.DetachAll();
        entities.Remove(entity);
    }

    // Empty the scene, detaching every component (edit-mode scene swaps). Play-mode lifecycle
    // teardown (FireEnd) happens in SceneManager.StopPlay before this is called.
    public void Clear()
    {
        foreach (Entity entity in entities)
            entity.DetachAll();
        entities.Clear();

        foreach (SceneBehaviour behaviour in sceneBehaviours)
            behaviour.OnDetach();
        sceneBehaviours.Clear();
    }

    // Run OnBegin/OnEnabled across the whole scene (entering play mode).
    internal void FireBegin()
    {
        foreach (Entity entity in entities.Where(entity => entity.IsActive))
            entity.FireBegin();
    }

    // Run OnDisabled across the whole scene (leaving play mode).
    internal void FireEnd()
    {
        foreach (Entity entity in entities.Where(entity => entity.IsActive))
            entity.FireEnd();
    }

    public void Update(in float deltaTime)
    {
        foreach (Entity entity in entities.Where(entity => entity.IsActive))
        {
            entity.Update(in deltaTime);
        }
    }
}
