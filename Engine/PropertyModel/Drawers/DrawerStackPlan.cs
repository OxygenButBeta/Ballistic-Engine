using System.Reflection;
using System.Runtime.CompilerServices;

namespace BallisticEngine;

public static class DrawerStackPlan {
    public static DrawerStackResolver Resolver { get; private set; } = BuildDefault();

    static readonly Dictionary<MemberInfo, DrawerStackResolver.MemberStack> cache = new();

    public static DrawerStackResolver.MemberStack For(MemberInfo member) {
        if (cache.TryGetValue(member, out DrawerStackResolver.MemberStack cached))
            return cached;
        DrawerStackResolver.MemberStack stack = Resolver.Resolve(member);
        cache[member] = stack;
        return stack;
    }

    public static void SetResolver(DrawerStackResolver resolver) {
        Resolver = resolver ?? BuildDefault();
        cache.Clear();
    }

    public static void Clear() => cache.Clear();

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(Clear);

    public static DrawerStackResolver BuildDefault() {
        var r = new DrawerStackResolver();

        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.Conditional.Visibility",
            Stage = DrawerStage.Visibility,
            Priority = 0,
            Applies = HasVisibilityCondition,
        });

        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.HeaderSpace",
            Stage = DrawerStage.Chrome,
            Priority = 0,
            Applies = m => m.GetCustomAttribute<HeaderAttribute>() is not null
                        || m.GetCustomAttribute<SpaceAttribute>() is not null,
        });

        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.Enable",
            Stage = DrawerStage.Enable,
            Priority = 0,
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null
                        || HasEnableCondition(m),
        });

        return r;
    }

    static bool HasVisibilityCondition(MemberInfo m) {
        foreach (ConditionalAttribute c in m.GetCustomAttributes<ConditionalAttribute>())
            if (c.Kind is ConditionKind.Show or ConditionKind.Hide) return true;
        return false;
    }

    static bool HasEnableCondition(MemberInfo m) {
        foreach (ConditionalAttribute c in m.GetCustomAttributes<ConditionalAttribute>())
            if (c.Kind is ConditionKind.Enable or ConditionKind.Disable) return true;
        return false;
    }
}
