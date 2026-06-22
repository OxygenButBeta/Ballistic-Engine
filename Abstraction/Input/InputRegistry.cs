namespace BallisticEngine.InputSystem;

public static class InputRegistry {
    static readonly List<InputAction> actions = new();
    static readonly Dictionary<string, InputAction> byName = new(StringComparer.Ordinal);

    public static IReadOnlyList<InputAction> All => actions;

    internal static void Register(InputAction action) {
        if (byName.TryGetValue(action.Name, out InputAction existing))
            actions.Remove(existing);
        byName[action.Name] = action;
        actions.Add(action);
    }

    public static InputAction Find(string name) =>
        name is null ? null : byName.GetValueOrDefault(name);

    public static void ClearForReload() {
        actions.Clear();
        byName.Clear();
    }

    public static void ScanForActions(params System.Reflection.Assembly[] assemblies) {
        foreach (System.Reflection.Assembly asm in assemblies.Where(a => a is not null).Distinct()) {
            foreach (Type type in SafeGetTypes(asm)) {
                bool hasActionField = type
                    .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Any(f => f.FieldType == typeof(InputAction));
                if (!hasActionField)
                    continue;
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
