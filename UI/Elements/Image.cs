namespace BallisticEngine.UI;

// An image element — analogue of HTML <img> / Unity's Image, and the target for the port skill's
// `style.backgroundImage = new StyleBackground(sprite)` pattern. Holds a reference to a UI texture
// the renderer blits into the element's box. The texture is an engine asset handle resolved through
// the normal AssetDatabase (a .png/.jpg under Assets/), kept as an opaque object here so the UI
// layer takes no GL or AssetPipeline dependency — the renderer/binding resolves it.
public class Image : VisualElement
{
    // The source texture (an engine Texture2D / RenderAsset handle). Object-typed to keep UI/ free of
    // GL + AssetPipeline references per the layering rules; the UI renderer casts it back.
    public object Texture { get; set; }

    public ScaleMode ScaleMode { get; set; } = ScaleMode.ScaleToFit;

    // Tint multiplied with the texture (CSS-like; white = unmodified). Defaults to opaque white.
    public Color Tint { get; set; } = Color.White;
}

// How the image fills its box. Mirrors CSS object-fit / Unity's -unity-background-scale-mode.
public enum ScaleMode
{
    StretchToFill,   // object-fit: fill
    ScaleToFit,      // object-fit: contain
    ScaleAndCrop,    // object-fit: cover
}
