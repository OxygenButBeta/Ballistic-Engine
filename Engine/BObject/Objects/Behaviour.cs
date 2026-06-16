namespace BallisticEngine;

public class Behaviour : Component {
    public bool IsEnabled {
        get => isEnabled;
        set {
            if (isEnabled == value) return;
            bool wasActive = IsActive;
            isEnabled = value;

            // Lifecycle runs in PLAY mode only (mirrors Entity.SetActive): in edit mode this just
            // flips the flag and the next frame's gather reflects it — OnBegin/OnEnabled are play-only.
            // Also held off during a scene deserialize (live reload), where the loader sets IsEnabled
            // from YAML before members are applied and fires Scene.FireBegin itself afterwards.
            if (!SceneManager.IsPlaying || SceneManager.SuppressPlayLifecycle)
                return;

            bool nowActive = IsActive;
            if (nowActive == wasActive)
                return; // a disabled-in-hierarchy entity: flipping IsEnabled changes no lifecycle now

            // Becoming active routes through FireEnable so a DEFERRED OnBegin runs once before the
            // first OnEnabled (a component that started IsEnabled=false and is enabled for the first
            // time still gets its Start). Disabling fires OnDisabled. Guarded like every dispatch site;
            // this setter is also ScriptGuard's auto-disable path, so it must not recurse on a throw.
            if (nowActive) {
                FireEnable();
            }
            else {
                FireDisable();
            }
        }
    }

    bool isEnabled = true;

    // ScriptGuard bookkeeping: consecutive faults of ONE callback (named by FaultCallback;
    // auto-disable threshold). The dispatch loops reset the streak only when the SAME callback
    // succeeds — a healthy FixedTick must not absolve a Tick that throws every frame.
    internal int FaultStreak;
    internal string FaultCallback;

    // Set when the component leaves its entity (RemoveComponent / scene teardown) so in-flight
    // dispatch snapshots skip it instead of ticking a detached component.
    internal bool IsDetached;

    // Unity's Start semantics: OnBegin fires exactly ONCE, the first time the component becomes
    // active in play mode — NOT at play-start for components that begin inactive. An entity spawned
    // (or left) disabled defers its OnBegin until it's first enabled; this flag tracks that so it
    // never double-fires and never gets skipped. Reset to false only by a full play teardown.
    internal bool HasBegun;

    // Gameplay-framework lifecycle (ITEM 0 gate / §5 phase runner): OnEnabled fires at most ONCE per
    // activation. Without this, FireEnable's OnEnabled is UNCONDITIONAL — so the phase runner, which
    // activates a framework component in Phase 1 (net strand) and again in Phase 3's scene.FireBegin
    // (Unity strand), would double-fire OnEnabled. HasEnabled goes true on the first OnEnabled and is
    // cleared on OnDisabled, so a later re-enable fires OnEnabled again (today's semantics). For every
    // EXISTING component this is byte-identical: they activate exactly once via FireBegin, so the flag
    // flips on that single call and changes nothing. Proven in %TEMP%\bal-gate-test (Docs/Plans/
    // gameplay-framework-item0-gate.md).
    internal bool HasEnabled;

    // Active = this component is enabled AND its entity is active all the way up the parent chain
    // (Unity's activeInHierarchy). The renderer's light/volume/draw gather loops test this, so a
    // disabled entity — or any disabled ancestor — drops everything beneath it.
    public bool IsActive => entity.IsActiveInHierarchy && IsEnabled;
    public Transform transform => entity.transform;

    // The entity this component lives on (Unity's .gameObject). Declared on the framework base
    // type, so ComponentReflection excludes it from serialization and the inspector.
    public Entity Entity => entity;

    // ---- Component lookup shortcuts (Unity's MonoBehaviour.GetComponent family) -------------
    // Forwarders to the owning entity so game scripts can write GetComponent<T>() against `this`
    // instead of Entity.GetComponent<T>(). Declared on the framework base so they stay out of
    // serialization/inspector.

    public T GetComponent<T>() where T : Behaviour => entity.GetComponent<T>();
    public bool TryGetComponent<T>(out T component) where T : Behaviour => entity.TryGetComponent(out component);
    public List<T> GetComponents<T>() where T : class => entity.GetComponents<T>();
    public T GetComponentInChildren<T>(bool includeInactive = false) where T : class =>
        entity.GetComponentInChildren<T>(includeInactive);
    public T GetComponentInParent<T>(bool includeInactive = false) where T : class =>
        entity.GetComponentInParent<T>(includeInactive);
    public T AddComponent<T>() where T : Behaviour, new() => entity.AddComponent<T>();

    // ---- Coroutines (Unity's MonoBehaviour.StartCoroutine) ----------------------------------
    // Forwarders to the global CoroutineRunner so scripts can write StartCoroutine(...) on `this`.
    // v1 note: these are NOT auto-cancelled when the component is disabled/destroyed (the global
    // runner owns them); cancel explicitly with StopCoroutine(handle), or stop on play exit.
    public CoroutineHandle StartCoroutine(IEnumerator<IYieldInstruction> routine) => Coroutine.Run(routine);
    public void StopCoroutine(CoroutineHandle handle) => Coroutine.Stop(handle);


    protected internal virtual void OnBegin() {
    }

    protected virtual void OnEnd() {
    }

    // Fires when the component is attached to an entity, in BOTH edit and play mode. Use this for
    // editor-visible registration (e.g. adding a renderer to a draw set) — distinct from OnEnabled,
    // which is play-mode game logic. OnDetach fires when the component/entity is torn down.
    protected internal virtual void OnAttach() {
    }

    protected internal virtual void OnDetach() {
    }

    protected internal virtual void OnEnabled() {
    }

    protected internal virtual void OnDisabled() {
    }

    // Unity-style activation: the FIRST time a component becomes active in play mode, OnBegin runs
    // (once, deferred if the entity started inactive) immediately before OnEnabled; every later
    // re-enable fires only OnEnabled. The single chokepoint for "this component just became active",
    // used by Scene.FireBegin (play start), Entity.Attach (added during play), and Entity.SetActive
    // (toggled on during play) — so a controller that spawns its camera in OnBegin works no matter
    // which of those paths first activates it. Exceptions are firewalled per callback via ScriptGuard.
    internal void FireEnable() {
        if (!HasBegun) {
            HasBegun = true;
            try { OnBegin(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnBegin", e); }
        }
        // Idempotent per activation (see HasEnabled): a second FireEnable within the same active span —
        // e.g. the §5 phase runner touching a framework component in both Phase 1 and Phase 3 — must NOT
        // re-fire OnEnabled. Cleared by FireDisable, so re-enable-after-disable still fires it.
        if (HasEnabled)
            return;
        HasEnabled = true;
        try { OnEnabled(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnEnabled", e); }
    }

    // The single OnDisabled dispatch site (the symmetric partner of FireEnable). Clears HasEnabled so a
    // later re-enable fires OnEnabled again. Every place that disables an active component routes here
    // (the IsEnabled setter, Entity.SetActive, RemoveComponent, FireEnd) so the HasEnabled invariant —
    // true exactly while OnEnabled has fired and OnDisabled has not — always holds.
    internal void FireDisable() {
        HasEnabled = false;
        try { OnDisabled(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnDisabled", e); }
    }

    protected internal virtual void Tick(in float delta) {
    }

    protected internal virtual void FixedTick(in float delta) {
    }

    // Physics contact callbacks (play mode only) — fired on the main thread right after each
    // fixed physics step, on every enabled behaviour of BOTH entities involved. Enter/Exit
    // pair up; Stay fires every step the contact persists. Sleeping contacts go quiet without
    // an Exit and resume Stay on wake (Unity semantics). The Trigger variants fire INSTEAD of
    // the Collision ones when either collider has IsTrigger set.
    // Game-assembly note: override these as `protected` (not `protected internal`) — the same
    // cross-assembly rule as Tick/OnBegin.
    protected internal virtual void OnCollisionEnter(Collision collision) {
    }

    protected internal virtual void OnCollisionStay(Collision collision) {
    }

    protected internal virtual void OnCollisionExit(Collision collision) {
    }

    protected internal virtual void OnTriggerEnter(Collider other) {
    }

    protected internal virtual void OnTriggerStay(Collider other) {
    }

    protected internal virtual void OnTriggerExit(Collider other) {
    }

    // Editor-only scene-view drawing. OnDrawGizmos runs for every active component each frame in the
    // Scene view; OnDrawGizmosSelected runs only for the components on the selected entity (and even
    // when the component is disabled), mirroring Unity. Both are no-ops at runtime — the editor is
    // the only caller. Implement them with the supplied IGizmos (no ImGui/GL needed).
    public virtual void OnDrawGizmos(IGizmos gizmos) {
    }

    public virtual void OnDrawGizmosSelected(IGizmos gizmos) {
    }
}