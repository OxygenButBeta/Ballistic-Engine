
namespace BallisticEngine;

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

    public BEvent<float> OnDamaged { get; set; } = new();

    public BEvent<float> OnHealed { get; set; } = new();

    public BEvent OnDied { get; set; } = new();

    [NotSerialized]
    public float CurrentHealth { get; set; } = -1f;

    [NotSerialized]
    public bool IsDead { get; private set; }

    [NotSerialized]
    public float HealthFraction => MaxHealth > 0f ? Math.Clamp(Current / MaxHealth, 0f, 1f) : 0f;

    float Current {
        get {
            if (CurrentHealth < 0f) { CurrentHealth = MaxHealth; }
            return CurrentHealth;
        }
        set => CurrentHealth = value;
    }

    protected internal override void OnBegin() {
        CurrentHealth = MaxHealth;
        IsDead = false;
    }

    public void TakeDamage(float amount) {
        if (amount <= 0f || Invulnerable || IsDead)
            return;

        float applied = MathF.Min(amount, Current);
        Current -= applied;
        OnDamaged?.Invoke(applied);

        if (Current <= 0f)
            Die();
    }

    public void Heal(float amount) {
        if (amount <= 0f || IsDead)
            return;

        float before = Current;
        Current = MathF.Min(Current + amount, MaxHealth);
        float restored = Current - before;
        if (restored > 0f)
            OnHealed?.Invoke(restored);
    }

    public void Kill() {
        if (IsDead) return;
        Current = 0f;
        Die();
    }

    public void Revive(float fraction = 1f) {
        IsDead = false;
        Current = Math.Clamp(fraction, 0f, 1f) * MaxHealth;
    }

    void Die() {
        if (IsDead) return;
        IsDead = true;
        Current = 0f;
        OnDied?.Invoke();

        if (DeathEffect is not null)
            BObjects.Instantiate(DeathEffect, transform.WorldPosition, transform.WorldRotation);

        if (DestroyOnDeath && SceneManager.IsPlaying)
            BObjects.Destroy(Entity);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        float h = HealthFraction;
        gizmos.Color = new Vector3(1f - h, h, 0.1f);
        gizmos.DrawWireSphere(transform.WorldPosition, 0.3f);
    }
}
