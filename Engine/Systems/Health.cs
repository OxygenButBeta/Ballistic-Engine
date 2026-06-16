
namespace BallisticEngine;

// Hit points with damage/heal/death (the gameplay companion to [[Spawner]] — spawned enemies, the
// player, destructibles). Exposes serialized BEvents (UnityEvent-style) so reactions are wired in the
// inspector with zero code — OnDamaged plays a hit sound, OnDied spawns an explosion / drops loot /
// disables the AI — the declarative, AI-managed-friendly pattern. Death can also auto-destroy the
// entity and/or spawn a death-effect prefab.
//
// CurrentHealth is runtime-only ([NotSerialized]); it initializes to MaxHealth when play begins (or on
// first access), so a freshly-spawned instance starts full. Damage/heal clamp to [0, MaxHealth]; the
// death transition fires OnDied exactly once (no re-fire until Revive).
[Component("Health", "Gameplay")]
public sealed class Health : Behaviour {
    [Tooltip("Maximum hit points. CurrentHealth starts here.")]
    [Range(1f, 100000f)]
    public float MaxHealth { get; set; } = 100f;

    [Tooltip("While true, TakeDamage is ignored (god mode / spawn-protection).")]
    public bool Invulnerable { get; set; }

    [Tooltip("Destroy the entity when health reaches 0.")]
    public bool DestroyOnDeath { get; set; } = true;

    [Tooltip("Optional prefab spawned at this entity's position on death (explosion, ragdoll, loot).")]
    public PrefabAsset DeathEffect { get; set; }

    // Fired when damage is taken (after clamping; carries the damage amount applied).
    public BEvent<float> OnDamaged { get; set; } = new();

    // Fired when healed (carries the amount restored).
    public BEvent<float> OnHealed { get; set; } = new();

    // Fired once when health crosses to 0.
    public BEvent OnDied { get; set; } = new();

    // Runtime current HP. -1 sentinel = "not yet initialized" so it snaps to MaxHealth on first use.
    [NotSerialized]
    public float CurrentHealth { get; set; } = -1f;

    [NotSerialized]
    public bool IsDead { get; private set; }

    // 0..1 for health bars.
    [NotSerialized]
    public float HealthFraction => MaxHealth > 0f ? Math.Clamp(Current / MaxHealth, 0f, 1f) : 0f;

    // CurrentHealth with lazy init to MaxHealth (so spawned/edit instances read full before OnBegin).
    float Current {
        get {
            if (CurrentHealth < 0f) { CurrentHealth = MaxHealth; }
            return CurrentHealth;
        }
        set => CurrentHealth = value;
    }

    protected internal override void OnBegin() {
        // Reset to full at the start of play (a spawned instance, or play after edit).
        CurrentHealth = MaxHealth;
        IsDead = false;
    }

    // Applies `amount` of damage (ignored if invulnerable, dead, or amount <= 0). Fires OnDamaged, and
    // OnDied if it drops to 0.
    public void TakeDamage(float amount) {
        if (amount <= 0f || Invulnerable || IsDead)
            return;

        float applied = MathF.Min(amount, Current);
        Current -= applied;
        OnDamaged?.Invoke(applied);

        if (Current <= 0f)
            Die();
    }

    // Restores `amount` (clamped to MaxHealth; ignored if dead — use Revive). Fires OnHealed.
    public void Heal(float amount) {
        if (amount <= 0f || IsDead)
            return;

        float before = Current;
        Current = MathF.Min(Current + amount, MaxHealth);
        float restored = Current - before;
        if (restored > 0f)
            OnHealed?.Invoke(restored);
    }

    // Instantly kills (sets health to 0 and runs the death transition). No-op if already dead.
    public void Kill() {
        if (IsDead) return;
        Current = 0f;
        Die();
    }

    // Brings a dead (or living) entity back to `fraction` of max health and clears the dead flag.
    public void Revive(float fraction = 1f) {
        IsDead = false;
        Current = Math.Clamp(fraction, 0f, 1f) * MaxHealth;
    }

    void Die() {
        if (IsDead) return; // fire exactly once
        IsDead = true;
        Current = 0f;
        OnDied?.Invoke();

        if (DeathEffect is not null)
            BObjects.Instantiate(DeathEffect, transform.WorldPosition, transform.WorldRotation);

        if (DestroyOnDeath && SceneManager.IsPlaying)
            BObjects.Destroy(Entity);
    }

    // Editor: a small health gizmo so a selected damaged entity shows its state.
    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        // green->red by remaining fraction.
        float h = HealthFraction;
        gizmos.Color = new Vector3(1f - h, h, 0.1f);
        gizmos.DrawWireSphere(transform.WorldPosition, 0.3f);
    }
}
