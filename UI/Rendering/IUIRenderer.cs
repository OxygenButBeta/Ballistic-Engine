
namespace BallisticEngine.UI;

// The render seam: the ONLY surface the UI tree draws against. A backend implements these primitives;
// the engine-side GL backend (added once the renderer rework settles) translates them into a 2D
// screen-space pass, and a headless recording stub implements them for tests. Keeping the contract
// this small (filled rounded rect, text, image, clip stack) means the GL fill-in is mechanical and
// the whole render-walk is verifiable without a GPU — the AI-first goal.
//
// All coordinates are in PANEL/LOGICAL pixels (top-left origin, +Y down). The backend applies the
// document's ResolvedScale as a uniform transform, so primitives never deal with screen scaling.
public interface IUIRenderer
{
    // Begin/end a document's draw list. size is the logical canvas; scale is UIDocument.ResolvedScale
    // (the backend bakes it into its projection). Between these, the walker issues the calls below.
    void Begin(Vector2 canvasSize, float scale);
    void End();

    // A filled, optionally rounded rectangle with an optional border. radius is per-corner
    // (TL, TR, BR, BL) in pixels, already clamped by the walker to <= half the min side (so a pill
    // request can't over-arc). borderWidth 0 = no border. Colors are straight RGBA (premultiplied
    // opacity already folded in by the walker).
    void DrawRect(Rect rect, Color fill, Vector4 radius, float borderWidth, Color borderColor);

    // A gradient-filled (optionally rounded) rectangle. `opacity` is the element's effective opacity
    // (the walker folds tree opacity in here rather than per-stop). Radius is per-corner, clamped by
    // the walker like DrawRect.
    void DrawGradient(Rect rect, Gradient gradient, Vector4 radius, float opacity);

    // A line of text within rect, styled by `style` (color, size, alignment, font family, letter
    // spacing, optional glow/shadow). Text rendering itself (SDF atlas) lands with the GL backend; the
    // stub just records the string.
    void DrawText(Rect rect, string text, in TextStyle style);

    // An image blitted into rect. texture is the opaque handle from Image.Texture (the backend casts
    // it to its engine texture type); tint multiplies the sampled color; scaleMode controls fit.
    void DrawImage(Rect rect, object texture, Color tint, ScaleMode scaleMode);

    // Clip stack: pixels outside the intersection of all pushed rects are not drawn. The walker pushes
    // a clip for any element with Overflow != Visible and pops it after its subtree. Backends intersect
    // with the current clip; an empty stack means "clip to the canvas".
    void PushClip(Rect rect);
    void PopClip();
}

// The resolved text styling the walker hands to DrawText — colour/size/alignment/font plus an
// optional glow/shadow. A struct (passed `in`) so it allocates nothing per draw. Colour is already
// premultiplied by the element's effective opacity.
public struct TextStyle
{
    public Color Color;
    public float FontSize;
    public TextAlign Align;
    public string FontFamily;
    public float LetterSpacing;

    // Optional drop-shadow / glow drawn behind the glyphs.
    public bool HasShadow;
    public float ShadowOffsetX, ShadowOffsetY, ShadowBlur;
    public Color ShadowColor;
}
