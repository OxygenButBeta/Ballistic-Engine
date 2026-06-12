using OpenTK.Mathematics;

namespace BallisticEngine;

// Spawns instances of a prefab over time within a spawn area (Unity's typical spawner pattern) — the
// gameplay workhorse for bullets, enemies, pickups, debris, ambient props. Optional OBJECT POOLING
// reuses expired instances instead of destroying/re-instantiating them (the big perf win for things
// that spawn constantly, like projectiles), via SetActive toggling rather than alloc churn.
//
// Spawns only in PLAY MODE (Tick). MaxAlive caps concurrent instances; Lifetime auto-expires them
// (0 = never). The spawn area is Point/Box/Sphere centered on the entity. Self-clocked accumulator
// drives the rate so fractional rates and frame-rate independence both work.
[Component("Spawner", "Gameplay")]
public sealed class Spawner : Behaviour {
    public enum Shape { Point, Box, Sphere }

    [Tooltip("The prefab to spawn. Drag a .prefab asset here.")]
    public PrefabAsset Prefab { get; set; }

    [Tooltip("Spawn this many per second (0 = only manual/burst spawning).")]
    [Range(0f, 100f)]
    public float SpawnRate { get; set; } = 2f;

    [Tooltip("Spawn a burst of this many the moment play begins.")]
    [Range(0, 1000)]
    public int SpawnOnAwake { get; set; }

    [Tooltip("Maximum instances alive at once (older ones are NOT auto-killed; spawning pauses at the cap).")]
    [Range(1, 10000)]
    public int MaxAlive { get; set; } = 50;

    [Tooltip("Seconds before an instance auto-expires (0 = never expires).")]
    [Range(0f, 120f)]
    public float Lifetime { get; set; } = 5f;

    [Tooltip("Reuse expired instances (SetActive toggle) instead of destroying them — perf for projectiles etc.")]
    public bool UsePooling { get; set; } = true;

    [Tooltip("Area instances spawn within, centered on this entity.")]
    public Shape SpawnShape { get; set; } = Shape.Point;

    [Tooltip("Box half-extents / sphere radius for the spawn area (world units).")]
    public Vector3 SpawnArea { get; set; } = new(1f, 0f, 1f);

    [Tooltip("Give each spawned instance a random Y rotation.")]
    public bool RandomYaw { get; set; }

    // Live instances and their remaining lifetime (parallel lists). Runtime-only.
    readonly List<Entity> alive = new();
    readonly List<float> ages = new();
    // Pooled (inactive) instances available for reuse.
    readonly List<Entity> pool = new();

    float accumulator;

    [NotSerialized]
    public int AliveCount => alive.Count;

    [NotSerialized]
    public int PooledCount => pool.Count;

    protected internal override void OnBegin() {
        for (var i = 0; i < SpawnOnAwake; i++)
            Spawn();
    }

    protected internal override void OnDisabled() => Clear();
    protected internal override void OnDetach() => Clear();

    protected internal override void Tick(in float delta) {
        // Age + expire live instances.
        for (var i = alive.Count - 1; i >= 0; i--) {
            Entity e = alive[i];
            if (e is null || e.IsDestroyed) { RemoveAt(i); continue; }
            if (Lifetime > 0f) {
                ages[i] += delta;
                if (ages[i] >= Lifetime) { Despawn(i); continue; }
            }
        }

        // Spawn at the configured rate, up to MaxAlive.
        if (SpawnRate > 0f && Prefab is not null) {
            accumulator += delta * SpawnRate;
            while (accumulator >= 1f && alive.Count < MaxAlive) {
                accumulator -= 1f;
                Spawn();
            }
            if (alive.Count >= MaxAlive)
                accumulator = 0f; // don't bank spawns while capped
        }
    }

    // Spawns one instance now (manual/burst). Respects MaxAlive. Returns the instance (or null).
    public Entity Spawn() {
        if (Prefab is null || alive.Count >= MaxAlive)
            return null;

        Vector3 pos = transform.WorldPosition + RandomPointInArea();
        Quaternion rot = RandomYaw
            ? Quaternion.FromAxisAngle(Vector3.UnitY, Random.Range(0f, MathF.Tau))
            : transform.WorldRotation;

        Entity e = TakeFromPool();
        if (e is not null) {
            e.transform.WorldPosition = pos;
            e.transform.WorldRotation = rot;
            e.SetActive(true);
        }
        else {
            e = Prefab.Instantiate(pos, rot);
            if (e is null) return null;
        }

        alive.Add(e);
        ages.Add(0f);
        return e;
    }

    // Removes/recycles every instance this spawner owns (called on disable/detach, or manually).
    public void Clear() {
        for (var i = alive.Count - 1; i >= 0; i--)
            Despawn(i);
        // Destroy any pooled instances too (we own them).
        foreach (Entity e in pool)
            if (e is not null && !e.IsDestroyed)
                BObjects.Destroy(e);
        pool.Clear();
        accumulator = 0f;
    }

    // ---- internals -----------------------------------------------------------

    void Despawn(int i) {
        Entity e = alive[i];
        RemoveAt(i);
        if (e is null || e.IsDestroyed) return;

        if (UsePooling) {
            e.SetActive(false);
            pool.Add(e);
        }
        else {
            BObjects.Destroy(e);
        }
    }

    void RemoveAt(int i) {
        alive.RemoveAt(i);
        ages.RemoveAt(i);
    }

    Entity TakeFromPool() {
        if (!UsePooling) return null;
        while (pool.Count > 0) {
            Entity e = pool[^1];
            pool.RemoveAt(pool.Count - 1);
            if (e is not null && !e.IsDestroyed)
                return e;
        }
        return null;
    }

    Vector3 RandomPointInArea() {
        switch (SpawnShape) {
            case Shape.Box:
                return new Vector3(
                    Random.Range(-SpawnArea.X, SpawnArea.X),
                    Random.Range(-SpawnArea.Y, SpawnArea.Y),
                    Random.Range(-SpawnArea.Z, SpawnArea.Z));
            case Shape.Sphere:
                return Random.InsideUnitSphere * SpawnArea.X;
            default:
                return Vector3.Zero;
        }
    }

    // Editor: draw the spawn area so it's visible/tweakable.
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(0.3f, 1f, 0.5f);
        Vector3 c = transform.WorldPosition;
        switch (SpawnShape) {
            case Shape.Box:
                gizmos.DrawWireCube(c, SpawnArea * 2f, transform.WorldRotation);
                break;
            case Shape.Sphere:
                gizmos.DrawWireSphere(c, SpawnArea.X);
                break;
            default:
                gizmos.DrawWireSphere(c, 0.15f);
                break;
        }
    }
}
