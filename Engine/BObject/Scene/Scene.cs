namespace BallisticEngine;

public class Scene : BObject
{
    // Scene is a container for all entities in the game world.
    // It manages the lifecycle of entities and their components.

    // Allocating a capacity of 200 entities for the scene. to ensure efficient memory usage.
    readonly List<Entity> entities = new(capacity: 200);

    public void RegisterEntity(Entity entity)
    {
        entities.Add(entity);
    }

    public void RemoveEntity(Entity entity)
    {
        entities.Remove(entity);
    }

    public void Update(in float deltaTime)
    {
        foreach (Entity entity in entities.Where(entity => entity.IsActive))
        {
            entity.Update(in deltaTime);
        }
    }
}