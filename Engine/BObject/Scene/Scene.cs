namespace BallisticEngine;

public class Scene : BObject
{
    readonly List<Entity> entities = new(capacity: 200);

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
        try { behaviour.OnAttach(); }
        catch (Exception e) { ScriptGuard.Report(behaviour, "OnAttach", e); }
        return behaviour;
    }

    public void RemoveSceneBehaviour(SceneBehaviour behaviour) {
        if (behaviour is null || !sceneBehaviours.Remove(behaviour))
            return;
        try { behaviour.OnDetach(); }
        catch (Exception e) { ScriptGuard.Report(behaviour, "OnDetach", e); }
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

    public Entity CreateEntity(string name = "Entity")
    {
        Entity entity = Entity.Instantiate(name);
        if (!entities.Contains(entity))
            entities.Add(entity);
        return entity;
    }

    public void DestroyEntity(Entity entity)
    {
        if (entity is null)
            return;

        var doomed = new List<Entity> { entity };
        foreach (Entity other in entities)
            if (!ReferenceEquals(other, entity) && other.transform.IsDescendantOf(entity.transform))
                doomed.Add(other);

        foreach (Entity victim in doomed)
        {
            victim.IsDestroyed = true;
            if (SceneManager.IsPlaying)
                victim.FireEnd();
            victim.DetachAll();
            entities.Remove(victim);
        }
    }

    public void Clear()
    {
        foreach (Entity entity in entities.ToArray())
            entity.DetachAll();
        entities.Clear();

        foreach (SceneBehaviour behaviour in sceneBehaviours.ToArray()) {
            try { behaviour.OnDetach(); }
            catch (Exception e) { ScriptGuard.Report(behaviour, "OnDetach", e); }
        }
        sceneBehaviours.Clear();
    }

    internal void FireBegin()
    {
        foreach (Entity entity in entities.ToArray())
            if (entity.IsActive && !entity.IsDestroyed)
                entity.FireBegin();
    }

    internal void FireEnd()
    {
        foreach (Entity entity in entities.ToArray())
            if (entity.IsActive && !entity.IsDestroyed)
                entity.FireEnd();
    }

    readonly List<Entity> updateSnapshot = new(capacity: 200);
    readonly List<Entity> fixedSnapshot = new(capacity: 200);

    public void Update(in float deltaTime)
    {
        updateSnapshot.Clear();
        updateSnapshot.AddRange(entities);
        for (int i = 0; i < updateSnapshot.Count; i++)
        {
            Entity entity = updateSnapshot[i];
            if (entity.IsActive && !entity.IsDestroyed)
                entity.Update(in deltaTime);
        }
    }

    public void FixedUpdate(in float fixedDelta)
    {
        fixedSnapshot.Clear();
        fixedSnapshot.AddRange(entities);
        for (int i = 0; i < fixedSnapshot.Count; i++)
        {
            Entity entity = fixedSnapshot[i];
            if (entity.IsActive && !entity.IsDestroyed)
                entity.FixedUpdate(in fixedDelta);
        }
    }
}
