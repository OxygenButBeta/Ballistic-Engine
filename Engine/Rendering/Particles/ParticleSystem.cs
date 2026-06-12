using OpenTK.Mathematics;

namespace BallisticEngine;

// How particle billboards composite (the GL pass picks the GL blend func from this).
public enum ParticleBlendMode {
    Additive,   // fire, sparks, magic — order-independent, brightens
    Alpha,      // smoke, dust — needs back-to-front sorting
}

// CPU-simulated, GPU-instanced billboard particle emitter (Unity's ParticleSystem, simplified). Spawns
// particles at EmissionRate, integrates velocity + gravity, lerps color/size over each particle's
// lifetime, and exposes a per-frame instance snapshot the GL particle pass renders as camera-facing
// billboards. This component (Engine layer) owns only the CPU sim + authored params; rendering lives
// in OpenGL/GLParticlePass.
//
// Simulation is driven from the renderer (ParticleSystem.AdvanceAll), NOT Tick — so particles also
// preview in the editor (Tick is play-only) and step exactly once per real frame regardless of how
// many times the editor renders. Registers in OnAttach/OnDetach (both modes) like a renderer.
[Component("Particle System", "Effects")]
public class ParticleSystem : Behaviour {
    [Header("Emission")]
    [Tooltip("Particles spawned per second.")]
    [Range(0f, 2000f)]
    public float EmissionRate { get; set; } = 30f;

    [Tooltip("Maximum live particles; emission stops at the cap.")]
    [Range(1, 100000)]
    public int MaxParticles { get; set; } = 1000;

    [Tooltip("Keep emitting forever. Off = emit for one StartLifetime then stop (one-shot burst feel).")]
    public bool Looping { get; set; } = true;

    [Header("Lifetime & motion")]
    [Tooltip("Seconds each particle lives.")]
    [Range(0.05f, 30f)]
    public float StartLifetime { get; set; } = 2f;

    [Tooltip("Initial speed along the emission direction.")]
    public float StartSpeed { get; set; } = 2f;

    [Tooltip("Constant acceleration (world units/s^2). Default pulls up like fire/smoke.")]
    public Vector3 Gravity { get; set; } = new(0f, 1f, 0f);

    [Tooltip("Cone half-angle in degrees around the emitter's local +Y. 0 = a straight beam, 180 = a full sphere.")]
    [Range(0f, 180f)]
    public float SpreadAngle { get; set; } = 25f;

    [Header("Appearance")]
    [Tooltip("Size at birth.")]
    [Range(0.001f, 50f)]
    public float StartSize { get; set; } = 0.5f;

    [Tooltip("Size at death (lerped over lifetime).")]
    [Range(0f, 50f)]
    public float EndSize { get; set; } = 0.1f;

    [Tooltip("Random +/- fraction applied to each particle's size at spawn.")]
    [Range(0f, 1f)]
    public float SizeJitter { get; set; } = 0.2f;

    [Tooltip("RGB color at birth.")]
    public Vector3 StartColor { get; set; } = new(1f, 0.6f, 0.2f);

    [Tooltip("Alpha at birth.")]
    [Range(0f, 1f)]
    public float StartAlpha { get; set; } = 1f;

    [Tooltip("RGB color at death (lerped over lifetime).")]
    public Vector3 EndColor { get; set; } = new(0.4f, 0.1f, 0.05f);

    [Tooltip("Alpha at death — fade out by setting this to 0.")]
    [Range(0f, 1f)]
    public float EndAlpha { get; set; }

    [Tooltip("How billboards composite. Additive = fire/sparks; Alpha = smoke.")]
    public ParticleBlendMode BlendMode { get; set; } = ParticleBlendMode.Additive;

    [Tooltip("Random spin speed range (radians/s) applied to billboards.")]
    [Range(0f, 12f)]
    public float RotationSpeed { get; set; }

    [Tooltip("Optional billboard texture. Drag an image asset; unassigned = a soft round dot.")]
    public Texture2D Texture { get; set; }

    // ---- Simulation state (runtime-only) ------------------------------------

    Particle[] pool;
    int liveCount;
    float emitAccumulator;
    float emitterAge;       // for non-looping one-shots
    ParticleInstance[] instanceScratch;

    [NotSerialized]
    public int LiveCount => liveCount;

    protected internal override void OnAttach() {
        if (!RuntimeSet<ParticleSystem>.Contains(this))
            RuntimeSet<ParticleSystem>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<ParticleSystem>.Remove(this);
    }

    // ---- Per-frame advance --------------------------------------------------

    // Steps every active particle system once. Called by the renderer with a once-per-frame guard so
    // the editor's double BeginRender doesn't double-step. dt is clamped to avoid first-frame spikes.
    public static void AdvanceAll(float dt) {
        dt = MathHelper.Clamp(dt, 0f, 0.1f);
        if (dt <= 0f)
            return;
        foreach (ParticleSystem system in RuntimeSet<ParticleSystem>.ReadOnlyCollection)
            if (system.IsActive)
                system.Advance(dt);
    }

    void Advance(float dt) {
        EnsurePool();

        // Integrate + age existing particles; swap-remove the dead.
        for (var i = 0; i < liveCount; i++) {
            ref Particle p = ref pool[i];
            p.Velocity += Gravity * dt;
            p.Position += p.Velocity * dt;
            p.Rotation += p.RotationSpeed * dt;
            p.Age += dt;
            if (p.IsDead) {
                pool[i] = pool[liveCount - 1];   // swap-remove
                liveCount--;
                i--;
            }
        }

        // Emit new particles from the accumulator (unless a finished one-shot).
        emitterAge += dt;
        bool emitting = Looping || emitterAge <= StartLifetime;
        if (emitting && EmissionRate > 0f) {
            emitAccumulator += EmissionRate * dt;
            while (emitAccumulator >= 1f && liveCount < MaxParticles) {
                emitAccumulator -= 1f;
                Spawn();
            }
            if (liveCount >= MaxParticles)
                emitAccumulator = 0f; // don't bank emissions we couldn't fulfill
        }
    }

    void Spawn() {
        Vector3 origin = transform.WorldPosition;
        Quaternion worldRot = transform.WorldRotation;

        // Random direction within the cone around local +Y, then into world space.
        Vector3 localDir = RandomConeDirection(SpreadAngle);
        Vector3 dir = worldRot * localDir;

        float sizeJ = 1f + Random.Range(-SizeJitter, SizeJitter);

        pool[liveCount++] = new Particle {
            Position = origin,
            Velocity = dir * StartSpeed,
            Age = 0f,
            Lifetime = StartLifetime,
            Rotation = Random.Range(0f, MathHelper.TwoPi),
            RotationSpeed = RotationSpeed * Random.Signed,
            StartSize = StartSize * sizeJ,
        };
    }

    // Uniformly-distributed direction within a cone of half-angle `degrees` around +Y.
    static Vector3 RandomConeDirection(float degrees) {
        float cosMax = MathF.Cos(MathHelper.DegreesToRadians(degrees));
        float cosTheta = 1f - Random.Value * (1f - cosMax);   // uniform over the spherical cap
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi = Random.Value * MathHelper.TwoPi;
        return new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
    }

    void EnsurePool() {
        if (pool is null || pool.Length != MaxParticles) {
            var resized = new Particle[MaxParticles];
            int keep = Math.Min(liveCount, MaxParticles);
            if (pool is not null)
                Array.Copy(pool, resized, keep);
            pool = resized;
            liveCount = keep;
        }
    }

    // ---- Render snapshot ----------------------------------------------------

    // Fills a per-frame instance snapshot (position, lerped size, lerped RGBA, rotation) for the GL
    // pass. Returns the live count; the array is the system's reused scratch (valid until next call).
    public int BuildInstances(out ParticleInstance[] instances) {
        if (instanceScratch is null || instanceScratch.Length < liveCount)
            instanceScratch = new ParticleInstance[Math.Max(liveCount, 16)];

        for (var i = 0; i < liveCount; i++) {
            ref Particle p = ref pool[i];
            float u = p.NormalizedAge;
            Vector3 rgb = Vector3.Lerp(StartColor, EndColor, u);
            float a = MathHelper.Lerp(StartAlpha, EndAlpha, u);
            float size = MathHelper.Lerp(p.StartSize, p.StartSize * SafeRatio(EndSize, StartSize), u);
            instanceScratch[i] = new ParticleInstance {
                Position = p.Position,
                Size = size,
                Color = new Vector4(rgb, a),
                Rotation = p.Rotation,
            };
        }
        instances = instanceScratch;
        return liveCount;
    }

    static float SafeRatio(float end, float start) => start > 1e-6f ? end / start : 1f;
}
