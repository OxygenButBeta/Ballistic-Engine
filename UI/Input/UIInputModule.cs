using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.UI;

// Turns raw pointer state from the engine's Input facade into VisualElement pointer events for one
// UI panel. Owned by the UIDocument; call Update(root, panelRect) once per frame after layout solves.
//
// It reads the SAME Input facade the rest of the engine does, so it inherits the editor's gating for
// free: Input.Enabled is false in edit mode / when the Game view isn't focused, so UI buttons don't
// react while you're editing — exactly the behaviour we want (CLAUDE.md: "Input.Enabled is the
// master gate"). The mouse position is converted from window space into panel space by the caller's
// panelRect so hit-testing matches the laid-out boxes.
//
// Responsibilities each frame:
//   * resolve the hovered element (LayoutPass.HitTest) and fire Enter/Leave across boundary changes,
//     maintaining IsHovered + a "hover" class for the cascade,
//   * on button press, remember the pressed element and mark IsPressed (+ "active" class),
//   * on release, fire Up everywhere and synthesize Click when release lands on the press target,
//     including raising Button.Clicked for Button elements (respecting Enabled).
public sealed class UIInputModule
{
    readonly PointerEvent _event = new();

    VisualElement _hovered;
    VisualElement _pressed;

    // Reusable hover/active state classes so the USS cascade can style :hover / :active without the
    // input module knowing anything about styling.
    const string HoverClass = "hover";
    const string ActiveClass = "active";

    // panelScreenRect: where the UI's render surface sits ON SCREEN, in the SAME space as
    // Input.MousePosition (the whole window for the player; the Game-view image's screen rect for the
    // editor). logicalSize: the UI's logical canvas (the space ResolvedRect lives in). The pointer is
    // mapped from the panel rect into logical space — (mouse - origin) * (logical / panelSize) — so the
    // hit-test matches the laid-out boxes regardless of the panel's on-screen offset OR display scale
    // (this is what fixes "the button is at y:50 but I have to click y:90"). Returns the hovered
    // element (or null).
    public VisualElement Update(VisualElement root, Rect panelScreenRect, Vector2 logicalSize)
    {
        if (root == null) return null;

        Vector2 mouse = Input.MousePosition;
        bool inside = Input.Enabled && Input.PointerInGameView && panelScreenRect.Contains(mouse);

        // Map window-space mouse -> logical UI space.
        float sx = panelScreenRect.Width > 0 ? logicalSize.X / panelScreenRect.Width : 1f;
        float sy = panelScreenRect.Height > 0 ? logicalSize.Y / panelScreenRect.Height : 1f;
        Vector2 local = new((mouse.X - panelScreenRect.X) * sx, (mouse.Y - panelScreenRect.Y) * sy);

        VisualElement hit = inside ? LayoutPass.HitTest(root, local) : null;

        UpdateHover(hit, local);
        UpdateButtons(hit, local);
        return hit;
    }

    void UpdateHover(VisualElement hit, Vector2 local)
    {
        if (ReferenceEquals(hit, _hovered)) return;

        if (_hovered != null)
        {
            _hovered.IsHovered = false;
            _hovered.RemoveFromClassList(HoverClass);
            _event.Reset(local, PointerButton.Left, _hovered);
            _hovered.FirePointerLeave(_event);
        }

        _hovered = hit;

        if (_hovered != null)
        {
            _hovered.IsHovered = true;
            _hovered.AddToClassList(HoverClass);
            _event.Reset(local, PointerButton.Left, _hovered);
            _hovered.FirePointerEnter(_event);
        }
    }

    void UpdateButtons(VisualElement hit, Vector2 local)
    {
        // Press: only the primary (left) button drives click semantics in v1; right/middle still
        // dispatch Down/Up with their button so handlers can implement context menus later.
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _pressed = hit;
            if (_pressed != null)
            {
                _pressed.IsPressed = true;
                _pressed.AddToClassList(ActiveClass);
                _event.Reset(local, PointerButton.Left, _pressed);
                _pressed.Bubble(_event, static (n, e) => n.FirePointerDown(e));
            }
        }

        // Release: fire Up on whatever the press landed on, clear active state, and if the pointer is
        // still over the same element, that's a Click (press+release on one target — the web rule).
        if (!Input.IsMouseButtonDown(MouseButton.Left) && _pressed != null)
        {
            var pressed = _pressed;
            _pressed = null;
            pressed.IsPressed = false;
            pressed.RemoveFromClassList(ActiveClass);

            _event.Reset(local, PointerButton.Left, pressed);
            pressed.Bubble(_event, static (n, e) => n.FirePointerUp(e));

            if (ReferenceEquals(hit, pressed))
            {
                _event.Reset(local, PointerButton.Left, pressed);
                pressed.Bubble(_event, static (n, e) => n.FirePointerClick(e));

                // A Button gets its high-level Clicked event too (unless disabled).
                if (pressed is Button b && b.Enabled)
                    b.InvokeClick();
            }
        }
    }

    // Clears all transient pointer state — call when the panel is hidden/disabled so a stale hover or
    // half-finished press doesn't survive (e.g. menu closes mid-press).
    public void Reset()
    {
        if (_hovered != null) { _hovered.IsHovered = false; _hovered.RemoveFromClassList(HoverClass); _hovered = null; }
        if (_pressed != null) { _pressed.IsPressed = false; _pressed.RemoveFromClassList(ActiveClass); _pressed = null; }
    }
}
