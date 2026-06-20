using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.UI;

// Turns raw input from the engine Input facade into VisualElement events for one UI panel (P3). Owned by
// the UIDocument; Update(root, panelRect, logicalSize) runs once per frame after layout.
//
// Subsystems:
//   * Pointer (P3.1): hover Enter/Leave (bubbling) + :hover state; per-button (L/M/R) Down/Up/Click that
//     bubble; PointerMove; pointer CAPTURE so a press that drags outside the element still streams Move/Up
//     to it (sliders, scrollbars, window drag); double-click detection.
//   * Wheel (P3.6): PointerWheel bubbles from the hovered element (feeds ScrollView).
//   * Focus (P3.2): click focuses the nearest Focusable; Tab/Shift-Tab walk the focus ring; Esc blurs;
//     :focus state class. Focused element receives keyboard.
//   * Keyboard (P3.3): KeyDown/KeyUp (edge-detected via Input.IsKeyPressed) + TextInput chars (the host
//     pushes typed chars via QueueChar — the OpenTK char callback, since the Input facade has no char API).
//
// IsHovered/IsPressed/IsFocused setters already flip the hover/active/focus classes + request a restyle
// (see VisualElement), so this module only sets those flags — it never touches classes directly.
public sealed class UIInputModule
{
    readonly PointerEvent _event = new();
    readonly KeyEvent _keyEvent = new();

    VisualElement _hovered;
    VisualElement _captured;                 // element holding pointer capture (press target until release)
    VisualElement _focused;
    readonly VisualElement[] _pressedBy = new VisualElement[3]; // press target per button (L/M/R)

    Vector2 _lastLocal;
    bool _hadLocal;

    // double-click bookkeeping (left button)
    VisualElement _lastClickTarget;
    float _timeSinceLastClick = 999f;
    const float DoubleClickSeconds = 0.3f;

    // typed chars pushed by the host this frame (OpenTK OnTextInput) — drained to the focused element.
    readonly Queue<char> _typedChars = new();

    // edge detection for keys we route to UI (the focused element gets all keys; we poll a working set).
    readonly HashSet<Keys> _keysDownLast = new();
    static readonly Keys[] WatchedKeys =
    {
        Keys.Backspace, Keys.Delete, Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End,
        Keys.Enter, Keys.KeyPadEnter, Keys.Escape, Keys.Tab, Keys.Space, Keys.A, Keys.C, Keys.V, Keys.X,
    };

    public VisualElement Focused => _focused;

    // Host pushes a typed character (from the window's text-input callback). Drained to the focused
    // element as TextInput each Update. Kept here so the UI layer needs no new Input-facade API.
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

        // While captured, the captured element is the logical hit even outside its box (drag tracking).
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

        // Press
        if (Input.IsMouseButtonPressed(mb))
        {
            _pressedBy[idx] = hit;
            if (hit != null)
            {
                if (isPrimary)
                {
                    hit.IsPressed = true;
                    _captured = hit;                 // capture for drag tracking (P3.1)
                    SetFocus(NearestFocusable(hit)); // click focuses (P3.2)
                }
                _event.Reset(local, pb, hit);
                hit.Bubble(_event, static (n, e) => n.FirePointerDown(e));
            }
            else if (isPrimary)
            {
                SetFocus(null);                      // click on empty space blurs
            }
        }

        // Release
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

            // Click = release over the SAME element the press started on (web rule).
            if (ReferenceEquals(hit, pressed))
            {
                _event.Reset(local, pb, pressed);
                pressed.Bubble(_event, static (n, e) => n.FirePointerClick(e));

                if (isPrimary)
                {
                    if (pressed is Button b && b.Enabled) b.InvokeClick();

                    // double-click (P3.1): a second click on the same target within the window.
                    if (ReferenceEquals(pressed, _lastClickTarget) && _timeSinceLastClick <= DoubleClickSeconds)
                    {
                        _event.Reset(local, pb, pressed);
                        pressed.Bubble(_event, static (n, e) => n.FirePointerDoubleClick(e));
                        _lastClickTarget = null;          // reset so a triple-click doesn't double-fire
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

    // ---- focus (P3.2) ----

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

    // ---- keyboard (P3.3) ----

    void UpdateKeyboard()
    {
        if (!Input.Enabled) return;

        // Tab / Shift-Tab move focus through the ring even with nothing focused.
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
        // Pull chars from the central Input facade (host pushes via Input.PushTypedChar) AND the local
        // QueueChar path (tests). Both feed the focused element; if nothing is focused, drop them.
        while (Input.TryReadTypedChar(out char fc)) _typedChars.Enqueue(fc);
        if (_focused == null) { _typedChars.Clear(); return; }
        while (_typedChars.Count > 0)
            _focused.FireTextInput(_typedChars.Dequeue());
    }

    // The focus ring: all Focusable elements in tree order, sorted by (TabIndex, order). Built per Tab.
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

    // Clears all transient state — call when the panel hides so stale hover/press/focus don't survive.
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
