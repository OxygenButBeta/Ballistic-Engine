using System.Reflection;

namespace BallisticEngine;

public sealed class PersistentListener {
    public enum CallMode { Void, Static, Dynamic }

    public Guid TargetId;

    public string MethodName;
    public CallMode Mode = CallMode.Void;

    public object StaticArgument;

    public Type StaticArgumentType;

    BObject resolvedTarget;

    public BObject ResolveTarget() {
        if (resolvedTarget is not null && resolvedTarget.InstanceId == TargetId)
            return resolvedTarget;
        resolvedTarget = SceneManager.FindByInstanceId(TargetId);
        return resolvedTarget;
    }

    internal void Invoke(BEvent source, object dynamicArg, bool hasDynamicArg) {
        BObject target = ResolveTarget();
        if (target is null)
            return;

        MethodInfo method = FindMethod(target.GetType());
        if (method is null) {
            Debugging.LogWarning(
                $"BEvent: target '{target.Name}' has no method '{MethodName}' matching mode {Mode}; skipped.");
            return;
        }

        object[] args = Mode switch {
            CallMode.Static  => [StaticArgument],
            CallMode.Dynamic => hasDynamicArg ? [dynamicArg] : null,
            _                => null,
        };
        if (Mode == CallMode.Dynamic && !hasDynamicArg)
            return;

        try { method.Invoke(target, args); }
        catch (TargetInvocationException tie) {
            Debugging.LogError($"BEvent: {target.GetType().Name}.{MethodName} threw:\n{tie.InnerException}");
        }
        catch (Exception e) {
            Debugging.LogError($"BEvent: failed to invoke {target.GetType().Name}.{MethodName}:\n{e}");
        }
    }

    MethodInfo FindMethod(Type targetType) {
        foreach (MethodInfo m in BEventReflection.InvokableMethods(targetType)) {
            if (m.Name != MethodName)
                continue;
            ParameterInfo[] ps = m.GetParameters();
            switch (Mode) {
                case CallMode.Void when ps.Length == 0:
                    return m;
                case CallMode.Static when ps.Length == 1 && (StaticArgumentType is null ||
                    ps[0].ParameterType.IsAssignableFrom(StaticArgumentType)):
                    return m;
                case CallMode.Dynamic when ps.Length == 1:
                    return m;
            }
        }
        return null;
    }
}
