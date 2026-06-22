namespace BallisticEngine.UI;

public class Image : VisualElement
{
    public object Texture { get; set; }

    public ScaleMode ScaleMode { get; set; } = ScaleMode.ScaleToFit;

    public Color Tint { get; set; } = Color.White;
}

public enum ScaleMode
{
    StretchToFill,
    ScaleToFit,
    ScaleAndCrop,
}
