public interface IDrawable {
    Transform Transform { get; }
    bool RenderedThisFrame { get; set; }
}