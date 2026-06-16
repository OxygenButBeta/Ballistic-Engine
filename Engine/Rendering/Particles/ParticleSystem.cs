
namespace BallisticEngine;

// How particle billboards composite (the GL pass picks the GL blend func from this).
public enum ParticleBlendMode {
    Additive,   // fire, sparks, magic — order-independent, brightens
    Alpha,      // smoke, dust — needs back-to-front sorting
}

// Shapes the over-lifetime interpolation of a particle property (size, color). Linear = the raw
// start->end lerp; the eased variants soften the motion (a real VFX usually wants size to ease out,
// alpha to ease in/out). Engine-local (no UI dependency).
public enum ParticleEase {
    Linear,
    EaseIn,     // slow start
    EaseOut,    // slow end (the common "fade/shrink gently" curve)
    EaseInOut,
}

// Where particles spawn and which way they head (all relative to the emitter's transform).
public enum EmissionShape {
    Cone,        // from a point, within a cone around local +Y (fire, fountains)
    Sphere,      // from the center, outward in every direction (explosions, bursts)
    Hemisphere,  // from the center, outward over the upper half (ground impacts)
    Box,         // from a random point in a box, heading up (rain, area fog)
    Circle,      // from a random point on a flat disc (XZ plane), heading up (smoke ring, portals)
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

    [Tooltip("Where particles spawn and which way they head. Cone = a point + cone (fire); Sphere = " +
             "outward everywhere (explosion); Box/Circle = a spawn volume/area.")]
    public EmissionShape Shape { get; set; } = EmissionShape.Cone;

    [Tooltip("Radius for Sphere/Hemisphere/Circle shapes (world units).")]
    [Range(0f, 100f)]
    public float ShapeRadius { get; set; } = 1f;

    [Tooltip("Half-extents for the Box shape (world units).")]
    public Vector3 BoxSize { get; set; } = new(1f, 1f, 1f);

    [Header("Burst")]
    [Tooltip("Particles released all at once at BurstTime each emitter cycle. 0 = no burst (explosions, impacts).")]
    [Range(0, 10000)]
    public int BurstCount { get; set; }

    [Tooltip("Seconds into each cycle when the burst fires. With Looping the cycle is StartLifetime long.")]
    [Range(0f, 30f)]
    public float BurstTime { get; set; }

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

    [Tooltip("Eases the size interpolation over lifetime. EaseOut shrinks gently at the end.")]
    public ParticleEase SizeEase { get; set; } = ParticleEase.Linear;

    [Tooltip("Optional size-over-lifetime curve (X = normalized age 0..1, Y = size MULTIPLIER on " +
             "StartSize). When it has keys it overrides the Start/EndSize+SizeEase lerp — author a " +
             "grow-then-shrink puff, a pulse, etc. Empty = use the simple lerp above.")]
    public AnimationCurve SizeCurve { get; set; } = new();

    [Tooltip("Optional color+alpha over-lifetime gradient (parameter = normalized age 0..1). When it " +
             "has keys it overrides the Start/EndColor+Alpha lerp — author fire, smoke, sparks. " +
             "Empty = use the Start/EndColor lerp above.")]
    public ColorGradient ColorOverLifetime { get; set; } = new();

    [Tooltip("Eases the color + alpha interpolation over lifetime. EaseOut fades gently at the end.")]
    public ParticleEase ColorEase { get; set; } = ParticleEase.Linear;

    [Tooltip("How billboards composite. Additive = fire/sparks; Alpha = smoke.")]
    public ParticleBlendMode BlendMode { get; set; } = ParticleBlendMode.Additive;

    [Tooltip("Random spin speed range (radians/s) applied to billboards.")]
    [Range(0f, 12f)]
    public float RotationSpeed { get; set; }

    [Tooltip("Optional billboard texture. Drag an image asset; unassigned = a soft round dot.")]
    public Texture2D Texture { get; set; }

    [Header("Texture sheet animation")]
    [Tooltip("Columns in the flipbook sprite sheet. 1 = no sheet animation.")]
    [Range(1, 32)]
    public int SheetTilesX { get; set; } = 1;

    [Tooltip("Rows in the flipbook sprite sheet. 1 = no sheet animation.")]
    [Range(1, 32)]
    public int SheetTilesY { get; set; } = 1;

    [Tooltip("How many times the flipbook plays over a particle's lifetime (1 = play once).")]
    [Range(0.1f, 20f)]
    public float SheetCycles { get; set; } = 1f;

    // ---- Simulation state (runtime-only) ------------------------------------

    Particle[] pool;
    int liveCount;
    float emitAccumulator;
    float emitterAge;       // for non-looping one-shots
    float cycleTime;        // time within the current burst cycle
    bool burstFiredThisCycle;
    ParticleInstance[] instanceScratch;

    [NotSerialized]
    public int LiveCount => liveCount;

    // Emits `count` particles immediately from the emitter (Unity's ParticleSystem.Emit). For
    // scripted one-off effects — muzzle flashes, hit sparks, footstep dust. Respects MaxParticles.
    public void Emit(int count) {
        EnsurePool();
        for (var i = 0; i < count && liveCount < MaxParticles; i++)
            Spawn();
    }

    // Kills all live particles and resets emission timing (Unity's ParticleSystem.Clear). Used by the
    // editor's restart button and by scripts that want a clean slate (e.g. on respawn).
    public void Clear() {
        liveCount = 0;
        emitAccumulator = 0f;
        emitterAge = 0f;
        cycleTime = 0f;
        burstFiredThisCycle = false;
    }

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

        emitterAge += dt;
        bool emitting = Looping || emitterAge <= StartLifetime;

        // Burst: fire BurstCount particles once per cycle when BurstTime is crossed. The cycle is
        // StartLifetime long (looping) or the whole one-shot; resets so a looping emitter re-bursts.
        if (BurstCount > 0 && emitting) {
            float cycleLength = Looping ? MathF.Max(StartLifetime, 0.001f) : float.MaxValue;
            cycleTime += dt;
            if (cycleTime >= cycleLength) {
                cycleTime -= cycleLength;
                burstFiredThisCycle = false;
            }
            if (!burstFiredThisCycle && cycleTime >= BurstTime) {
                burstFiredThisCycle = true;
                Emit(BurstCount);
            }
        }

        // Continuous emission from the accumulator (unless a finished one-shot).
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
        if (liveCount >= MaxParticles)
            return;

        Vector3 worldOrigin = transform.WorldPosition;
        Quaternion worldRot = transform.WorldRotation;

        // Pick a LOCAL spawn offset + emission direction per shape (around local +Y), then world it.
        ShapeSample(out Vector3 localPos, out Vector3 localDir);
        Vector3 position = worldOrigin + Vector3.Transform(localPos, worldRot);
        Vector3 dir = Vector3.Transform(localDir, worldRot);

        float sizeJ = 1f + Random.Range(-SizeJitter, SizeJitter);

        pool[liveCount++] = new Particle {
            Position = position,
            Velocity = dir * StartSpeed,
            Age = 0f,
            Lifetime = StartLifetime,
            Rotation = Random.Range(0f, MathHelper.TwoPi),
            RotationSpeed = RotationSpeed * Random.Signed,
            StartSize = StartSize * sizeJ,
        };
    }

    // Local-space spawn position + unit emission direction for the current shape (emitter +Y is "up").
    void ShapeSample(out Vector3 position, out Vector3 direction) {
        switch (Shape) {
            case EmissionShape.Sphere: {
                // From the center, outward in every direction.
                position = Vector3.Zero;
                direction = Random.OnUnitSphere;
                break;
            }
            case EmissionShape.Hemisphere: {
                position = Vector3.Zero;
                Vector3 d = Random.OnUnitSphere;
                direction = new Vector3(d.X, MathF.Abs(d.Y), d.Z); // upper half
                break;
            }
            case EmissionShape.Box: {
                position = new Vector3(
                    Random.Signed * BoxSize.X,
                    Random.Signed * BoxSize.Y,
                    Random.Signed * BoxSize.Z);
                direction = Vector3.UnitY;
                break;
            }
            case EmissionShape.Circle: {
                // Random point on a flat disc in the XZ plane (uniform by sqrt-radius).
                float r = ShapeRadius * MathF.Sqrt(Random.Value);
                float a = Random.Value * MathHelper.TwoPi;
                position = new Vector3(r * MathF.Cos(a), 0f, r * MathF.Sin(a));
                direction = Vector3.UnitY;
                break;
            }
            default: { // Cone
                position = Vector3.Zero;
                direction = RandomConeDirection(SpreadAngle);
                break;
            }
        }
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

        int tilesX = Math.Max(1, SheetTilesX);
        int tilesY = Math.Max(1, SheetTilesY);
        int tileCount = tilesX * tilesY;
        bool animated = tileCount > 1;
        float invX = 1f / tilesX, invY = 1f / tilesY;

        for (var i = 0; i < liveCount; i++) {
            ref Particle p = ref pool[i];
            float u = p.NormalizedAge;
            float uc = ApplyEase(ColorEase, u);
            float us = ApplyEase(SizeEase, u);
            // A gradient (when authored) drives color+alpha over the normalized age; otherwise fall
            // back to the simple Start->End color/alpha lerp with its ease.
            Vector3 rgb;
            float a;
            if (ColorOverLifetime is { IsEmpty: false }) {
                Vector4 c = ColorOverLifetime.Evaluate(u);
                rgb = new Vector3(c.X, c.Y, c.Z);
                a = c.W;
            }
            else {
                rgb = Vector3.Lerp(StartColor, EndColor, uc);
                a = MathHelper.Lerp(StartAlpha, EndAlpha, uc);
            }
            // A curve (when authored) drives size as a MULTIPLIER on the spawn size over the
            // normalized age; otherwise fall back to the simple Start->End lerp with its ease.
            float size = SizeCurve is { Count: > 0 }
                ? p.StartSize * SizeCurve.Evaluate(u)
                : MathHelper.Lerp(p.StartSize, p.StartSize * SafeRatio(EndSize, StartSize), us);

            Vector4 uvRect = new(0f, 0f, 1f, 1f);
            if (animated) {
                // Frame index marches through the grid over the lifetime, SheetCycles times. Top-left
                // origin (V flipped) so frame 0 is the sheet's top-left cell.
                int frame = (int)(u * SheetCycles * tileCount) % tileCount;
                int col = frame % tilesX;
                int row = frame / tilesX;
                uvRect = new Vector4(col * invX, 1f - (row + 1) * invY, invX, invY);
            }

            instanceScratch[i] = new ParticleInstance {
                Position = p.Position,
                Size = size,
                Color = new Vector4(rgb, a),
                Rotation = p.Rotation,
                UvRect = uvRect,
            };
        }
        instances = instanceScratch;
        return liveCount;
    }

    static float SafeRatio(float end, float start) => start > 1e-6f ? end / start : 1f;

    // Maps a 0..1 lerp parameter through the ease curve (cubic, closed-form — no per-frame solve).
    // Endpoints are preserved (0->0, 1->1) so Start/End values always hit exactly.
    static float ApplyEase(ParticleEase ease, float t) {
        t = Math.Clamp(t, 0f, 1f);
        return ease switch {
            ParticleEase.EaseIn => t * t * t,
            ParticleEase.EaseOut => 1f - MathF.Pow(1f - t, 3f),
            ParticleEase.EaseInOut => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
            _ => t,
        };
    }

    // Selected: draw the emission shape so you can see where particles spawn + which way they head,
    // in world space (emitter transform). Cone = apex + cone along +Y; Sphere/Hemisphere = a sphere;
    // Box = a wire box; Circle = a flat ring (approximated by a thin box on the XZ plane).
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.7f, 0.2f);
        Vector3 origin = transform.WorldPosition;
        Quaternion rot = transform.WorldRotation;
        Vector3 up = Vector3.Transform(Vector3.UnitY, rot);

        switch (Shape) {
            case EmissionShape.Cone:
                gizmos.DrawWireCone(origin, up * MathF.Max(StartSpeed, 1f),
                    MathHelper.Clamp(SpreadAngle, 1f, 89f));
                break;
            case EmissionShape.Sphere:
                gizmos.DrawWireSphere(origin, MathF.Max(ShapeRadius, 0.05f));
                break;
            case EmissionShape.Hemisphere:
                gizmos.DrawWireSphere(origin, MathF.Max(ShapeRadius, 0.05f));
                gizmos.DrawRay(origin, up * ShapeRadius);
                break;
            case EmissionShape.Box:
                gizmos.DrawWireCube(origin, BoxSize * 2f, rot);
                break;
            case EmissionShape.Circle:
                gizmos.DrawWireCube(origin, new Vector3(ShapeRadius * 2f, 0.02f, ShapeRadius * 2f), rot);
                break;
        }
    }
}
