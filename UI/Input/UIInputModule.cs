using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.UI;

public sealed class UIInputModule
{
    readonly PointerEvent _event = new();
    readonly KeyEvent _keyEvent = new();

    VisualElement _hovered;
    VisualElement _captured;
    VisualElement _focused;
    readonly VisualElement[] _pressedBy = new VisualElement[3];

    Vector2 _lastLocal;
    bool _hadLocal;

    VisualElement _lastClickTarget;
    float _timeSinceLastClick = 999f;
    const float DoubleClickSeconds = 0.3f;

    readonly Queue<char> _typedChars = new();

    readonly HashSet<Keys> _keysDownLast = new();
    static readonly Keys[] WatchedKeys =
    {
        Keys.Backspace, Keys.Delete, Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End,
        Keys.Enter, Keys.KeyPadEnter, Keys.Escape, Keys.Tab, Keys.Space, Keys.A, Keys.C, Keys.V, Keys.X,
    };

    public VisualElement Focused => _focused;

    public void QueueChar(char c) => _typedChars.Enqueue(c);

    public VisualElement Update(VisualElement root, Rect panelScreenRect, Vector2 logicalSize, float dt = 0f)
    {
        if (root == null) return null;
        _timeSinceLastClick += dt;

        Vector2 mouse = Input.MousePosition;
        bool inside = Input.Enabled && Input.PointerInGameView && panelScreenRect.Contains(mouse);

        float sx = panelScreenRect.Width > 0 ? logicalSize.X / panelScreenRect.Width : 1f;
        float sy = panelScreenRect.Height > 0 ? logicalSize.Y / panelScreenRect.Height : 1f;
        Vector2 local = new((mouse.X - panelScreenRect.X) * sx, (mouse.Y - panelScreenRect.Y) * sy);

        VisualElement hit = inside ? LayoutPass.HitTest(root, local) : null;

        UpdatePointerMove(local);
        UpdateHover(_captured ?? hit, local);
        UpdateButtons(hit, local);
        UpdateWheel(_captured ?? hit, local);
        UpdateKeyboard();
        DrainText();

        _lastLocal = local; _hadLocal = true;
        return hit;
    }

    void UpdatePointerMove(Vector2 local)
    {
        if (!_hadLocal) { _lastLocal = local; return; }
        Vector2 delta = new(local.X - _lastLocal.X, local.Y - _lastLocal.Y);
        if (delta.X == 0f && delta.Y == 0f) return;
        var target = _captured ?? _hovered;
        if (target == null) return;
        _event.Reset(local, PointerButton.Left, target);
        _event.Delta = delta;
        target.Bubble(_event, static (n, e) => n.FirePointerMove(e));
    }

    void UpdateHover(VisualElement hit, Vector2 local)
    {
        if (ReferenceEquals(hit, _hovered)) return;

        if (_hovered != null)
        {
            _hovered.IsHovered = false;
            _event.Reset(local, PointerButton.Left, _hovered);
            _hovered.Bubble(_event, static (n, e) => n.FirePointerLeave(e));
        }

        _hovered = hit;

        if (_hovered != null)
        {
            _hovered.IsHovered = true;
            _event.Reset(local, PointerButton.Left, _hovered);
            _hovered.Bubble(_event, static (n, e) => n.FirePointerEnter(e));
        }
    }

    void UpdateButtons(VisualElement hit, Vector2 local)
    {
        HandleButton(MouseButton.Left, PointerButton.Left, hit, local, isPrimary: true);
        HandleButton(MouseButton.Right, PointerButton.Right, hit, local, isPrimary: false);
        HandleButton(MouseButton.Middle, PointerButton.Middle, hit, local, isPrimary: false);
    }

    void HandleButton(MouseButton mb, PointerButton pb, VisualElement hit, Vector2 local, bool isPrimary)
    {
        int idx = (int)pb;

        if (Input.IsMouseButtonPressed(mb))
        {
            _pressedBy[idx] = hit;
            if (hit != null)
            {
                if (isPrimary)
                {
                    hit.IsPressed = true;
                    _captured = hit;
                    SetFocus(NearestFocusable(hit));
                }
                _event.Reset(local, pb, hit);
                hit.Bubble(_event, static (n, e) => n.FirePointerDown(e));
            }
            else if (isPrimary)
            {
                SetFocus(null);
            }
        }

        if (!Input.IsMouseButtonDown(mb) && _pressedBy[idx] != null)
        {
            var pressed = _pressedBy[idx];
            _pressedBy[idx] = null;

            if (isPrimary)
            {
                pressed.IsPressed = false;
                _captured = null;
            }

            _event.Reset(local, pb, pressed);
            pressed.Bubble(_event, static (n, e) => n.FirePointerUp(e));

            if (ReferenceEquals(hit, pressed))
            {
                _event.Reset(local, pb, pressed);
                pressed.Bubble(_event, static (n, e) => n.FirePointerClick(e));

                if (isPrimary)
                {
                    if (pressed is Button b && b.Enabled) b.InvokeClick();

                    if (ReferenceEquals(pressed, _lastClickTarget) && _timeSinceLastClick <= DoubleClickSeconds)
                    {
                        _event.Reset(local, pb, pressed);
                        pressed.Bubble(_event, static (n, e) => n.FirePointerDoubleClick(e));
                        _lastClickTarget = null;
                        _timeSinceLastClick = 999f;
                    }
                    else
                    {
                        _lastClickTarget = pressed;
                        _timeSinceLastClick = 0f;
                    }
                }
            }
        }
    }

    void UpdateWheel(VisualElement target, Vector2 local)
    {
        Vector2 scroll = Input.ScrollDelta;
        if ((scroll.X == 0f && scroll.Y == 0f) || target == null) return;
        _event.Reset(local, PointerButton.Left, target);
        _event.ScrollDelta = scroll;
        target.Bubble(_event, static (n, e) => n.FirePointerWheel(e));
    }

    public void SetFocus(VisualElement el)
    {
        if (ReferenceEquals(el, _focused)) return;
        if (_focused != null) { _focused.IsFocused = false; _focused.FireFocusOut(); }
        _focused = el;
        if (_focused != null) { _focused.IsFocused = true; _focused.FireFocusIn(); }
    }

    static VisualElement NearestFocusable(VisualElement el)
    {
        for (var n = el; n != null; n = n.Parent)
            if (n.Focusable) return n;
        return null;
    }

    void UpdateKeyboard()
    {
        if (!Input.Enabled) return;

        if (Input.IsKeyPressed(Keys.Tab))
        {
            bool shift = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
            MoveFocus(shift ? -1 : 1);
        }
        if (Input.IsKeyPressed(Keys.Escape))
            SetFocus(null);

        if (_focused == null) return;

        bool sh = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift);
        bool ct = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl);
        bool al = Input.IsKeyDown(Keys.LeftAlt) || Input.IsKeyDown(Keys.RightAlt);

        foreach (var k in WatchedKeys)
        {
            bool down = Input.IsKeyDown(k);
            bool was = _keysDownLast.Contains(k);
            if (down && !was)
            {
                _keysDownLast.Add(k);
                _keyEvent.Reset(k, sh, ct, al, _focused);
                BubbleKey(_focused, _keyEvent, true);
            }
            else if (!down && was)
            {
                _keysDownLast.Remove(k);
                _keyEvent.Reset(k, sh, ct, al, _focused);
                BubbleKey(_focused, _keyEvent, false);
            }
        }
    }

    static void BubbleKey(VisualElement el, KeyEvent e, bool down)
    {
        var node = el;
        while (node != null && !e.Handled)
        {
            if (down) node.FireKeyDown(e); else node.FireKeyUp(e);
            node = node.Parent;
        }
    }

    void DrainText()
    {
        while (Input.TryReadTypedChar(out char fc)) _typedChars.Enqueue(fc);
        if (_focused == null) { _typedChars.Clear(); return; }
        while (_typedChars.Count > 0)
            _focused.FireTextInput(_typedChars.Dequeue());
    }

    void MoveFocus(int dir)
    {
        var ring = new List<VisualElement>();
        CollectFocusable(RootOf(_focused), ring);
        if (ring.Count == 0) return;
        ring.Sort((a, b) => a.TabIndex != b.TabIndex ? a.TabIndex.CompareTo(b.TabIndex) : 0);

        int cur = _focused != null ? ring.IndexOf(_focused) : -1;
        int next = cur < 0 ? (dir > 0 ? 0 : ring.Count - 1) : ((cur + dir) % ring.Count + ring.Count) % ring.Count;
        SetFocus(ring[next]);
    }

    static VisualElement RootOf(VisualElement el)
    {
        if (el == null) return null;
        var n = el; while (n.Parent != null) n = n.Parent; return n;
    }

    static void CollectFocusable(VisualElement el, List<VisualElement> into)
    {
        if (el == null) return;
        if (el.Focusable && el.Style.Display != DisplayStyle.None) into.Add(el);
        var kids = el.Children;
        for (int i = 0; i < kids.Count; i++) CollectFocusable(kids[i], into);
    }

    public void Reset()
    {
        if (_hovered != null) { _hovered.IsHovered = false; _hovered = null; }
        for (int i = 0; i < _pressedBy.Length; i++)
        {
            if (_pressedBy[i] != null) { _pressedBy[i].IsPressed = false; _pressedBy[i] = null; }
        }
        _captured = null;
        SetFocus(null);
        _typedChars.Clear();
        _keysDownLast.Clear();
    }
}
