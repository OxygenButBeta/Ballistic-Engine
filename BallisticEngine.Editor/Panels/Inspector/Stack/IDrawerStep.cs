namespace BallisticEngine.Editor.Inspector;

public interface IDrawerStep {
    string Key { get; }

    bool Draw(IProperty property, IInspectorGui gui, Func<bool> next);
}
