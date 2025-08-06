namespace BallisticEngine;

public class Material : BObject {
    public Texture2D Texture { get; set; }
    public Shader Shader { get; set; }

    public Material(Texture2D texture, Shader shader) {
        Texture = texture;
        Shader = shader;
    }
}