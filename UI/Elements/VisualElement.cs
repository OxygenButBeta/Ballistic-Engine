namespace BallisticEngine.UI;

public class VisualElement
{
    public string Name { get; set; }

    readonly List<string> _classes = new();
    public IReadOnlyList<string> ClassList => _classes;

    public virtual string TypeName => GetType().Name;

    public string InlineStyle { get; set; }

    public VisualElement Parent { get; private set; }
    readonly List<VisualElement> _children = new();
    public IReadOnlyList<VisualElement> Children => _children;

    internal LayoutNode Layout { get; } = new();
    public Style Style { get; }

    public Rect ResolvedRect { get; internal set; }

    public bool PickingEnabled { get; set; } = true;

    public event System.Action<PointerEvent> PointerEnter;
    public event System.Action<PointerEvent> PointerLeave;
    public event System.Action<PointerEvent> PointerDown;
    public event System.Action<PointerEvent> PointerUp;
    public event System.Action<PointerEvent> PointerClick;
    public event System.Action<PointerEvent> PointerDoubleClick;
    public event System.Action<PointerEvent> PointerMove;
    public event System.Action<PointerEvent> PointerWheel;

    public event System.Action<KeyEvent> KeyDown;
    public event System.Action<KeyEvent> KeyUp;
    public event System.Action<char> TextInput;

    public event System.Action FocusIn;
    public event System.Action FocusOut;

    public bool Focusable { get; set; }
    public int TabIndex { get; set; }

    public object UserData { get; set; }
    public int UserIndex { get; set; }

    public string Role { get; set; }
    public string AccessibleLabel { get; set; }

    internal void FirePointerDoubleClick(PointerEvent e) => PointerDoubleClick?.Invoke(e);
    internal void FirePointerMove(PointerEvent e) => PointerMove?.Invoke(e);
    internal void FirePointerWheel(PointerEvent e) => PointerWheel?.Invoke(e);
    internal void FireKeyDown(KeyEvent e) => KeyDown?.Invoke(e);
    internal void FireKeyUp(KeyEvent e) => KeyUp?.Invoke(e);
    internal void FireTextInput(char c) => TextInput?.Invoke(c);
    internal void FireFocusIn() => FocusIn?.Invoke();
    internal void FireFocusOut() => FocusOut?.Invoke();

    internal UIDocument OwnerDocument;

    internal void RequestRestyle() => OwnerDocument?.MarkRestyleDirty(this);

    bool _isHovered, _isPressed;
    public bool IsHovered
    {
        get => _isHovered;
        internal set { if (_isHovered == value) return; _isHovered = value; EnableInClassList("hover", value); }
    }
    public bool IsPressed
    {
        get => _isPressed;
        internal set { if (_isPressed == value) return; _isPressed = value; EnableInClassList("active", value); }
    }

    bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        internal set { if (_isFocused == value) return; _isFocused = value; EnableInClassList("focus", value); }
    }

    internal void FirePointerEnter(PointerEvent e) => PointerEnter?.Invoke(e);
    internal void FirePointerLeave(PointerEvent e) => PointerLeave?.Invoke(e);
    internal void FirePointerDown(PointerEvent e) => PointerDown?.Invoke(e);
    internal void FirePointerUp(PointerEvent e) => PointerUp?.Invoke(e);
    internal void FirePointerClick(PointerEvent e) => PointerClick?.Invoke(e);

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
        Style = new Style(this);
    }

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

    public void AddToClassList(string className)
    {
        if (string.IsNullOrEmpty(className) || _classes.Contains(className)) return;
        _classes.Add(className);
        RequestRestyle();
    }

    public void RemoveFromClassList(string className)
    {
        if (_classes.Remove(className)) RequestRestyle();
    }

    public bool ClassListContains(string className) => _classes.Contains(className);

    public void EnableInClassList(string className, bool enabled)
    {
        if (enabled) AddToClassList(className);
        else RemoveFromClassList(className);
    }

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
