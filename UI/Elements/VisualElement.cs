using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// The retained-tree node — Ballistic's analogue of a DOM element / Unity UI Toolkit's VisualElement.
// A ported Claude design becomes a tree of these: the UXML loader builds the static skeleton, the
// USS cascade writes computed style into each one, and the C# controller mutates them via the same
// Q<>() + class-list + style API the port skill's Unity controllers use, so ported code reads 1:1.
//
// Each element owns a LayoutNode (the Yoga facade) kept structurally in sync with the visual tree;
// after the root solves layout, ResolvedRect holds the final pixel box. Drawing is deferred — the
// element exposes its computed visual Style + ResolvedRect, and the (later) IUIRenderer walks the
// tree to emit quads/text. Nothing here touches GL.
public class VisualElement
{
    // --- identity & classification (USS / UXML / Q<>) ---

    // The UXML name="..." — the stable handle controllers use to find an element once via Q<>().
    public string Name { get; set; }

    readonly List<string> _classes = new();
    public IReadOnlyList<string> ClassList => _classes;

    // The element type name used as a USS type selector (e.g. "Button", "Label"). Defaults to the
    // runtime type name so subclasses get a sensible selector for free.
    public virtual string TypeName => GetType().Name;

    // The raw inline declaration block from a UXML style="..." attribute, kept so the UIDocument can
    // RE-APPLY it after the USS cascade — preserving CSS precedence (inline beats stylesheet). Null
    // when the element had no inline style. Set by the UXML loader.
    public string InlineStyle { get; internal set; }

    // --- tree ---

    public VisualElement Parent { get; private set; }
    readonly List<VisualElement> _children = new();
    public IReadOnlyList<VisualElement> Children => _children;

    // --- layout + computed style ---

    internal LayoutNode Layout { get; } = new();
    public Style Style { get; }

    // Final pixel box in PANEL space (absolute, top-left origin), filled by the layout pass after
    // the root solves. Valid only post-layout; the renderer reads this.
    public Rect ResolvedRect { get; internal set; }

    // Pointer events skip this element (and it doesn't steal clicks from siblings/children below)
    // when false — the equivalent of the port skill's picking-mode="Ignore" for visual overlays.
    public bool PickingEnabled { get; set; } = true;

    // --- pointer event callbacks (the UIInputModule drives these) ---
    // Plain C# events, not a generic event-bus — ported controllers subscribe directly, e.g.
    // `row.PointerDown += e => Select(item);`, mirroring the source design's onClick handlers.
    // PointerEnter/Leave fire as the pointer crosses element boundaries (drives :hover styling);
    // PointerDown/Up/Click fire on button transitions; Click means press+release on the same element.
    public event System.Action<PointerEvent> PointerEnter;
    public event System.Action<PointerEvent> PointerLeave;
    public event System.Action<PointerEvent> PointerDown;
    public event System.Action<PointerEvent> PointerUp;
    public event System.Action<PointerEvent> PointerClick;

    // Whether the pointer is currently over this element — kept in sync by the input module so the
    // cascade can apply :hover, and so a subclass can react. Read-only to the outside.
    public bool IsHovered { get; internal set; }
    public bool IsPressed { get; internal set; }

    internal void FirePointerEnter(PointerEvent e) => PointerEnter?.Invoke(e);
    internal void FirePointerLeave(PointerEvent e) => PointerLeave?.Invoke(e);
    internal void FirePointerDown(PointerEvent e) => PointerDown?.Invoke(e);
    internal void FirePointerUp(PointerEvent e) => PointerUp?.Invoke(e);
    internal void FirePointerClick(PointerEvent e) => PointerClick?.Invoke(e);

    // Walks self → ancestors firing `fire` on each until one marks the event Handled (DOM-style
    // bubbling). Used by the input module so a click on a child can be caught by a parent row.
    internal void Bubble(PointerEvent e, System.Action<VisualElement, PointerEvent> fire)
    {
        var node = this;
        while (node != null && !e.Handled)
        {
            fire(node, e);
            node = node.Parent;
        }
    }

    public VisualElement()
    {
        // Style writes through to this element's LayoutNode for layout properties and stores visual
        // properties (colors, radius, etc.) for the renderer. See Style.cs.
        Style = new Style(this);
    }

    // --- tree mutation (keeps the Yoga child list in lockstep) ---

    public void Add(VisualElement child)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));
        if (child.Parent == this) return;
        child.RemoveFromHierarchy();

        child.Parent = this;
        _children.Add(child);
        Layout.InsertChild(child.Layout, _children.Count - 1);
    }

    public void Insert(int index, VisualElement child)
    {
        if (child == null) throw new ArgumentNullException(nameof(child));
        child.RemoveFromHierarchy();
        index = Math.Clamp(index, 0, _children.Count);

        child.Parent = this;
        _children.Insert(index, child);
        Layout.InsertChild(child.Layout, index);
    }

    public void Remove(VisualElement child)
    {
        if (child == null || child.Parent != this) return;
        _children.Remove(child);
        Layout.RemoveChild(child.Layout);
        child.Parent = null;
    }

    public void RemoveFromHierarchy() => Parent?.Remove(this);

    public void Clear()
    {
        for (int i = _children.Count - 1; i >= 0; i--)
            _children[i].Parent = null;
        _children.Clear();
        Layout.RemoveAllChildren();
    }

    public int ChildCount => _children.Count;

    // --- class list (USS matching + EnableInClassList state toggling) ---

    public void AddToClassList(string className)
    {
        if (string.IsNullOrEmpty(className) || _classes.Contains(className)) return;
        _classes.Add(className);
    }

    public void RemoveFromClassList(string className) => _classes.Remove(className);

    public bool ClassListContains(string className) => _classes.Contains(className);

    // The workhorse for state styling in ported controllers (:hover/:active/selected/etc are toggled
    // as classes): add or remove `className` to match `enabled` in one call. Mirrors Unity's API name
    // so ported code is copy-paste compatible.
    public void EnableInClassList(string className, bool enabled)
    {
        if (enabled) AddToClassList(className);
        else RemoveFromClassList(className);
    }

    // --- querying (Q<>) ---

    // Depth-first search for the first descendant matching name and/or type. Both filters optional:
    // Q<Label>("title") finds a Label named "title"; Q("title") finds any element by name; Q<Button>()
    // finds the first Button. This is the one-call-in-constructor pattern the port skill relies on.
    public T Q<T>(string name = null) where T : VisualElement
    {
        foreach (var d in Descendants())
            if (d is T typed && (name == null || d.Name == name))
                return typed;
        return null;
    }

    public VisualElement Q(string name)
    {
        foreach (var d in Descendants())
            if (d.Name == name)
                return d;
        return null;
    }

    // Pre-order traversal of the subtree below this element (excludes self).
    public IEnumerable<VisualElement> Descendants()
    {
        foreach (var c in _children)
        {
            yield return c;
            foreach (var sub in c.Descendants())
                yield return sub;
        }
    }
}
