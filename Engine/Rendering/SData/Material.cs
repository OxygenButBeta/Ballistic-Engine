namespace BallisticEngine;

public class Material : BObject {
    public Texture2D Albedo { get; set; }
    public Shader Shader { get; set; }

    public Material(Texture2D albedo, Shader shader) {
        Albedo = albedo;
        Shader = shader;
    }
}