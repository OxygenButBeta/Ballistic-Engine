namespace BallisticEngine;

public class Behaviour : Component {
    public bool IsEnabled {
        get => isEnabled;
        set {
            if (isEnabled == value) return;
            bool wasActive = IsActive;
            isEnabled = value;

            if (!SceneManager.IsPlaying || SceneManager.SuppressPlayLifecycle)
                return;

            bool nowActive = IsActive;
            if (nowActive == wasActive)
                return;

            if (nowActive) {
                FireEnable();
            }
            else {
                FireDisable();
            }
        }
    }

    bool isEnabled = true;

    internal int FaultStreak;
    internal string FaultCallback;

    internal bool IsDetached;

    internal bool HasBegun;

    internal bool HasEnabled;

    public bool IsActive => entity.IsActiveInHierarchy && IsEnabled;
    public Transform transform => entity.transform;

    public Entity Entity => entity;

    public T GetComponent<T>() where T : Behaviour => entity.GetComponent<T>();
    public bool TryGetComponent<T>(out T component) where T : Behaviour => entity.TryGetComponent(out component);
    public List<T> GetComponents<T>() where T : class => entity.GetComponents<T>();
    public T GetComponentInChildren<T>(bool includeInactive = false) where T : class =>
        entity.GetComponentInChildren<T>(includeInactive);
    public T GetComponentInParent<T>(bool includeInactive = false) where T : class =>
        entity.GetComponentInParent<T>(includeInactive);
    public T AddComponent<T>() where T : Behaviour, new() => entity.AddComponent<T>();

    public CoroutineHandle StartCoroutine(IEnumerator<IYieldInstruction> routine) => Coroutine.Run(routine);
    public void StopCoroutine(CoroutineHandle handle) => Coroutine.Stop(handle);


    protected internal virtual void OnBegin() {
    }

    protected virtual void OnEnd() {
    }

    protected internal virtual void OnAttach() {
    }

    protected internal virtual void OnDetach() {
    }

    protected internal virtual void OnEnabled() {
    }

    protected internal virtual void OnDisabled() {
    }

    internal void FireEnable() {
        if (!HasBegun) {
            HasBegun = true;
            try { OnBegin(); }
            catch (Exception e) { ScriptGuard.Report(this, "OnBegin", e); }
        }

        if (HasEnabled)
            return;
        HasEnabled = true;
        try { OnEnabled(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnEnabled", e); }
    }

    internal void FireDisable() {
        HasEnabled = false;
        try { OnDisabled(); }
        catch (Exception e) { ScriptGuard.Report(this, "OnDisabled", e); }
    }

    protected internal virtual void Tick(in float delta) {
    }

    protected internal virtual void FixedTick(in float delta) {
    }

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

    public virtual void OnDrawGizmos(IGizmos gizmos) {
    }

    public virtual void OnDrawGizmosSelected(IGizmos gizmos) {
    }
}