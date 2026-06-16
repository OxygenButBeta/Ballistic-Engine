namespace BallisticEngine.InputSystem;

// The fusion registry every InputAction self-registers into (plan §7.3.1). A host-side static — so it
// is one of the TWO new roots that pin the collectible script-ALC if not cleared at the reload
// boundary (§8.6.2 / gate 0c). ClearForReload() joins the existing "clear scene + registry + volume
// stack before GameScripts.Unload" list in EngineBootstrap.ReloadGameScripts — without it the first
// hot-reload leaks the old assembly via a game-defined action.
//
// `static readonly InputAction` fields are LAZY (C# runs initializers on first touch). A per-binding
// SetupInput touches the action it uses, so that path self-registers fine; a "list ALL actions" rebind
// screen needs the load-time scan (ScanForActions) that touches every action-container type once at
// bootstrap (like ComponentRegistry.Build). Documented so a future rebind UI doesn't miss untouched
// actions (§7.3.1 gotcha).
public static class InputRegistry {
    static readonly List<InputAction> actions = new();
    static readonly Dictionary<string, InputAction> byName = new(StringComparer.Ordinal);

    public static IReadOnlyList<InputAction> All => actions;

    internal static void Register(InputAction action) {
        // Idempotent on name: a hot-reload re-runs a field initializer (new ALC) — the new instance
        // replaces the old by name. (ClearForReload empties the table first, so in practice this is a
        // fresh insert each reload.)
        if (byName.TryGetValue(action.Name, out InputAction existing))
            actions.Remove(existing);
        byName[action.Name] = action;
        actions.Add(action);
    }

    public static InputAction Find(string name) =>
        name is null ? null : byName.GetValueOrDefault(name);

    // THE 0c contract: drop every registered action so no script-ALC InputAction handle survives the
    // reload (the ALC can unload). Called from ReloadGameScripts alongside VolumeManager.ResetStack /
    // ComponentRegistry rebuild. The next assembly's field initializers re-register on first touch /
    // the load-time scan.
    public static void ClearForReload() {
        actions.Clear();
        byName.Clear();
    }

    // Load-time scan: force every action-container type's static fields to initialize so the full
    // action list is populated up front (for a rebind screen). Touches each type's fields by running
    // its static ctor. Run once at bootstrap, never per-frame. P0 wires this in EngineBootstrap after
    // the component registry build; a container type is any with public static readonly InputAction
    // fields. (Engine + game assemblies, like ComponentRegistry.Build.)
    public static void ScanForActions(params System.Reflection.Assembly[] assemblies) {
        foreach (System.Reflection.Assembly asm in assemblies.Where(a => a is not null).Distinct()) {
            foreach (Type type in SafeGetTypes(asm)) {
                bool hasActionField = type
                    .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Any(f => f.FieldType == typeof(InputAction));
                if (!hasActionField)
                    continue;
                // Touch the type → runs its static initializers → InputAction ctors self-register.
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
        }
    }

    static Type[] SafeGetTypes(System.Reflection.Assembly asm) {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e) {
            return e.Types.Where(t => t is not null).ToArray();
        }
    }
}
