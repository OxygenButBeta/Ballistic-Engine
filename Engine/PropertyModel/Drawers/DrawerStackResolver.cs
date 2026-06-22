using System.Reflection;
using System.Runtime.CompilerServices;

namespace BallisticEngine;

public sealed class DrawerStackResolver {
    public sealed class Descriptor {
        public required string Key { get; init; }
        public required DrawerStage Stage { get; init; }
        public required int Priority { get; init; }
        public required Func<MemberInfo, bool> Applies { get; init; }
        public bool IsTerminal => Stage == DrawerStage.Terminal;
    }

    readonly List<Descriptor> descriptors = new();

    public sealed class MemberStack {
        public required MemberInfo Member { get; init; }
        public required IReadOnlyList<Descriptor> Steps { get; init; }
        public Descriptor Terminal => Steps.Count > 0 && Steps[^1].IsTerminal ? Steps[^1] : null;
        public bool HasTerminal => Terminal is not null;
    }

    public int Count => descriptors.Count;

    public void Register(Descriptor d) => descriptors.Add(d);

    public void RegisterAttribute<TAttr>(DrawerStage stage, int priority = 0, string key = null)
        where TAttr : Attribute =>
        Register(new Descriptor {
            Key = key ?? typeof(TAttr).FullName,
            Stage = stage,
            Priority = priority,
            Applies = m => m.GetCustomAttribute<TAttr>() is not null,
        });

    public MemberStack Resolve(MemberInfo member) {
        var nonTerminal = new List<Descriptor>();
        var terminals = new DeterministicResolver<Descriptor>();

        foreach (Descriptor d in descriptors) {
            if (!d.Applies(member)) continue;
            if (d.IsTerminal) terminals.Register(d, d.Priority, d.Key);
            else nonTerminal.Add(d);
        }

        IEnumerable<Descriptor> ordered = nonTerminal
            .OrderBy(d => (int)d.Stage)
            .ThenByDescending(d => d.Priority)
            .ThenBy(d => d.Key, StringComparer.Ordinal);

        var steps = ordered.ToList();

        Descriptor terminal = terminals.Resolve(_ => true);
        if (terminal is not null)
            steps.Add(terminal);

        return new MemberStack { Member = member, Steps = steps };
    }
}

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
