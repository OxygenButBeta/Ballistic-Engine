namespace BallisticEngine;

public class Scene : BObject
{
    // Scene is a container for all entities in the game world.
    // It manages the lifecycle of entities and their components.

    // Allocating a capacity of 200 entities for the scene. to ensure efficient memory usage.
    readonly List<Entity> entities = new(capacity: 200);

    public IReadOnlyList<Entity> Entities => entities;

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

    // Remove an entity. In play mode also tear down its components so it leaves any RuntimeSets.
    public void DestroyEntity(Entity entity)
    {
        if (entity is null)
            return;

        if (SceneManager.IsPlaying)
            entity.FireEnd();
        entities.Remove(entity);
    }

    // Empty the scene without firing lifecycle (used for edit-mode scene swaps).
    // Play-mode teardown happens in SceneManager.StopPlay before this is called.
    public void Clear()
    {
        entities.Clear();
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
