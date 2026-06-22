using System.Reflection;

namespace BallisticEngine.Editor.Inspector;

public sealed class DrawerStack {
    readonly DrawerRegistry registry;
    readonly Dictionary<string, IDrawerStep> stepsByKey;
    readonly IDrawerStep terminal;

    DrawerStack(DrawerRegistry registry, IReadOnlyList<IDrawerStep> nonTerminalSteps, IDrawerStep terminal) {
        this.registry = registry;
        this.terminal = terminal;
        stepsByKey = new Dictionary<string, IDrawerStep>();
        foreach (IDrawerStep s in nonTerminalSteps)
            stepsByKey[s.Key] = s;
    }

    public DrawerRegistry Registry => registry;

    public static DrawerStack CreateDefault(DrawerRegistry registry = null) {
        DrawerRegistry reg = registry ?? DrawerRegistry.CreatePrimitive();
        return new DrawerStack(
            reg,
            new IDrawerStep[] { new VisibilityStep(), new HeaderSpaceStep(), new EnableStep() },
            new TypeDrawerTerminalStep(reg));
    }

    public static DrawerStack CreateComponent(DrawerRegistry registry = null) {
        DrawerRegistry reg = registry ?? DrawerRegistry.CreatePrimitive();
        return new DrawerStack(
            reg,
            new IDrawerStep[] { new EnableStep() },
            new TypeDrawerTerminalStep(reg));
    }

    public bool Draw(IProperty property, IInspectorGui gui) {
        Staged staged = ResolveStaged(property);

        if (!RunVisibility(staged.Visibility, property, gui))
            return false;

        foreach (IDrawerStep chrome in staged.Chrome)
            chrome.Draw(property, gui, AlwaysTrue);

        gui.PushId(property.Name);
        gui.BeginRow(property);
        try {
            Func<bool> drawTerminal = () => terminal.Draw(property, gui, NoNext);
            Func<bool> enabled = drawTerminal;
            for (int i = staged.Enable.Count - 1; i >= 0; i--) {
                IDrawerStep step = staged.Enable[i];
                Func<bool> inner = enabled;
                enabled = () => step.Draw(property, gui, inner);
            }
            return enabled();
        } finally {
            gui.EndRow();
            gui.PopId();
        }
    }

    static bool RunVisibility(IReadOnlyList<IDrawerStep> visSteps, IProperty p, IInspectorGui gui) {
        if (visSteps.Count == 0) return true;
        Func<bool> next = AlwaysTrue;
        for (int i = visSteps.Count - 1; i >= 0; i--) {
            IDrawerStep step = visSteps[i];
            Func<bool> inner = next;
            next = () => step.Draw(p, gui, inner);
        }
        return next();
    }

    static bool AlwaysTrue() => true;
    static bool NoNext() => false;

    readonly record struct Staged(
        IReadOnlyList<IDrawerStep> Visibility,
        IReadOnlyList<IDrawerStep> Chrome,
        IReadOnlyList<IDrawerStep> Enable);

    Staged ResolveStaged(IProperty property) {
        var vis = new List<IDrawerStep>();
        var chrome = new List<IDrawerStep>();
        var enable = new List<IDrawerStep>();

        MemberInfo member = StackMemberLookup.MemberOf(property);
        if (member is null)
            return new Staged(vis, chrome, enable);

        foreach (DrawerStackResolver.Descriptor d in DrawerStackPlan.For(member).Steps) {
            if (d.IsTerminal) continue;
            if (!stepsByKey.TryGetValue(d.Key, out IDrawerStep step)) continue;
            switch (d.Stage) {
                case DrawerStage.Visibility: vis.Add(step); break;
                case DrawerStage.Chrome:     chrome.Add(step); break;
                case DrawerStage.Enable:     enable.Add(step); break;
            }
        }
        return new Staged(vis, chrome, enable);
    }
}
