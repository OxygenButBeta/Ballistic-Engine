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

    // Create an entity that belongs to THIS scene (Entity.Instantiate registers into the
    // current scene; this lets the editor target a specific scene explicitly).
    public Entity CreateEntity(string name = "Entity")
    {
        Entity entity = Entity.Instantiate(name);
        if (!entities.Contains(entity))
            entities.Add(entity);
        return entity;
    }

    // Remove an entity AND its transform descendants (children are meaningless without their
    // parent — deleting a model root must not strand its per-mesh children), detaching all
    // components so renderers leave their draw sets.
    public void DestroyEntity(Entity entity)
    {
        if (entity is null)
            return;

        // Snapshot first: removing while scanning would skip grandchildren.
        var doomed = new List<Entity> { entity };
        foreach (Entity other in entities)
            if (!ReferenceEquals(other, entity) && other.transform.IsDescendantOf(entity.transform))
                doomed.Add(other);

        foreach (Entity victim in doomed)
        {
            victim.IsDestroyed = true; // before teardown: this frame's dispatch snapshots skip it
            if (SceneManager.IsPlaying)
                victim.FireEnd();
            victim.DetachAll();
            entities.Remove(victim);
        }
    }

    // Empty the scene, detaching every component (edit-mode scene swaps). Play-mode lifecycle
    // teardown (FireEnd) happens in SceneManager.StopPlay before this is called.
    public void Clear()
    {
        // Snapshots: a guarded OnDetach may legally remove other components/entities.
        foreach (Entity entity in entities.ToArray())
            entity.DetachAll();
        entities.Clear();

        foreach (SceneBehaviour behaviour in sceneBehaviours.ToArray()) {
            try { behaviour.OnDetach(); }
            catch (Exception e) { ScriptGuard.Report(behaviour, "OnDetach", e); }
        }
        sceneBehaviours.Clear();
    }

    // Run OnBegin/OnEnabled across the whole scene (entering play mode). Iterates a snapshot:
    // a component's OnBegin may Instantiate new entities (e.g. a player controller spawning its
    // camera), which appends to `entities` — that must not invalidate this enumeration. The newly
    // spawned entities run their own lifecycle immediately via AddComponent (SceneManager.IsPlaying),
    // so they aren't skipped.
    internal void FireBegin()
    {
        foreach (Entity entity in entities.ToArray())
            if (entity.IsActive && !entity.IsDestroyed)
                entity.FireBegin();
    }

    // Run OnDisabled across the whole scene (leaving play mode).
    internal void FireEnd()
    {
        foreach (Entity entity in entities.ToArray())
            if (entity.IsActive && !entity.IsDestroyed)
                entity.FireEnd();
    }

    // Reused snapshot buffers for the per-frame Update / FixedUpdate sweeps. The OLD code did
    // `entities.ToArray()` every call — a fresh heap array per frame (and per fixed step, 0–4×/frame),
    // which on a CPU-heavy scene with many entities is steady GC pressure (and a GC spike is a frame
    // hitch, the thing that hurts a real game more than a fraction of a ms of average). Copying into a
    // reused list (Clear + AddRange) is allocation-free once the buffer has grown to the entity count.
    // We STILL snapshot (don't iterate `entities` directly) so a behaviour that spawns/destroys an entity
    // mid-tick — normal gameplay — can't invalidate the sweep. Separate buffers for Update vs FixedUpdate
    // because a FixedUpdate can run nested inside the physics step within a frame; they never alias.
    readonly List<Entity> updateSnapshot = new(capacity: 200);
    readonly List<Entity> fixedSnapshot = new(capacity: 200);

    // A newly spawned entity ran its own OnBegin/OnEnabled via AddComponent already; it starts ticking
    // next frame. An entity destroyed mid-frame stays in the snapshot but is skipped via IsDestroyed
    // (its components already tore down). Index loop (not foreach) avoids the List enumerator too.
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

    // Runs FixedTick across the scene; called by the fixed-step physics loop, before each step.
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
