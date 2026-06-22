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
