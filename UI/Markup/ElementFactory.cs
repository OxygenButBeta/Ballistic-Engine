using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// Maps UXML tag names to VisualElement subclasses. Ported designs use the same tag vocabulary every
// time (Panel/Label/Button/Image plus a few HTML-flavoured aliases), so the loader resolves a tag to
// a constructor here. Unknown tags fall back to a plain Panel and log — a port should never crash on
// an unrecognised element, just degrade to an empty container (Unity-style resilience).
//
// Register custom element types (e.g. a game's own ProgressBar) with Register("ProgressBar", () => …)
// at startup and they become usable in .uxml with zero further wiring.
public static class ElementFactory
{
    static readonly Dictionary<string, Func<VisualElement>> _factories =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Native element names.
        ["VisualElement"] = () => new Panel(),
        ["Panel"] = () => new Panel(),
        ["Label"] = () => new Label(),
        ["Button"] = () => new Button(),
        ["Image"] = () => new Image(),

        // HTML aliases so a design pasted closer to its source still resolves. <div> is the bare
        // container, <span>/<p> are text, <img> is an image, <button> is a button.
        ["div"] = () => new Panel(),
        ["span"] = () => new Label(),
        ["p"] = () => new Label(),
        ["img"] = () => new Image(),
    };

    public static void Register(string tag, Func<VisualElement> factory)
    {
        if (string.IsNullOrEmpty(tag) || factory == null) return;
        _factories[tag] = factory;
    }

    public static bool IsKnown(string tag) => _factories.ContainsKey(tag);

    // Creates an element for `tag`, or a Panel fallback (logged) for an unknown tag.
    public static VisualElement Create(string tag)
    {
        if (_factories.TryGetValue(tag, out var f))
            return f();

        Debugging.LogWarning($"UXML: unknown element <{tag}>, substituting an empty Panel.");
        return new Panel();
    }
}
