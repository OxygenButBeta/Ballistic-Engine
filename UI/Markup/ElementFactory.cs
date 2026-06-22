namespace BallisticEngine.UI;

public static class ElementFactory
{
    static readonly Dictionary<string, Func<VisualElement>> _factories =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["VisualElement"] = () => new Panel(),
        ["Panel"] = () => new Panel(),
        ["Label"] = () => new Label(),
        ["Button"] = () => new Button(),
        ["Image"] = () => new Image(),
        ["div"] = () => new Panel(),
        ["span"] = () => new Label(),
        ["p"] = () => new Label(),
        ["img"] = () => new Image(),
        ["ScrollView"] = () => new ScrollView(),
        ["TextField"] = () => new TextField(),
        ["Toggle"] = () => new Toggle(),
        ["Slider"] = () => new Slider(),
        ["Dropdown"] = () => new Dropdown(),
        ["DropdownField"] = () => new Dropdown(),
        ["Foldout"] = () => new Foldout(),
        ["TabView"] = () => new TabView(),
        ["ListView"] = () => new ListView(),
        ["ProgressBar"] = () => new ProgressBar(),
    };

    public static void Register(string tag, Func<VisualElement> factory)
    {
        if (string.IsNullOrEmpty(tag) || factory == null) return;
        _factories[tag] = factory;
    }

    public static bool IsKnown(string tag) => _factories.ContainsKey(tag);

    public static VisualElement Create(string tag)
    {
        if (_factories.TryGetValue(tag, out var f))
            return f();

        Debugging.LogWarning($"UXML: unknown element <{tag}>, substituting an empty Panel.");
        return new Panel();
    }
}
