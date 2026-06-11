using OpenTK.Mathematics;

namespace BallisticEngine;

public class Material : BObject
{
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Texture2D Metallic { get; set; }
    public Texture2D Roughness { get; set; }
    public Texture2D AO { get; set; }
    public Texture2D Emissive { get; set; }
    public Shader Shader { get; set; }

    public Vector3 EmissiveColor { get; set; } = Vector3.One;
    public float EmissiveIntensity { get; set; } = 1f;

    // Transparent materials render in a sorted back-to-front pass with alpha blending.
    public bool Transparent { get; set; }
    public float Opacity { get; set; } = 1f;

    Material(Shader shader, Texture2D diffuse, Texture2D normal, Texture2D metallic, Texture2D roughness,
        Texture2D ao, Texture2D emissive)
    {
        Diffuse = diffuse;
        Normal = normal;
        Shader = shader;
        Metallic = metallic;
        Roughness = roughness;
        AO = ao;
        Emissive = emissive;
    }

    public static Material Create(StandardShader standardShader, Texture2D diffuse, Texture2D normal = null,
        Texture2D metallic = null, Texture2D roughness = null, Texture2D ao = null, Texture2D emissive = null)
    {
        return new Material(standardShader, diffuse, normal, metallic, roughness, ao, emissive);
    }

    public void Activate()
    {
        // Always re-activate the shader: other passes (skybox, post-process) bind their own
        // programs between draws, and uniform uploads target whatever program is current.
        Shader.Activate();
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        Diffuse.Activate();
        Metallic?.Activate();
        Normal?.Activate();
        AO?.Activate();
        Roughness?.Activate();
        Emissive?.Activate();
    }

    public void Deactivate()
    {
        if (!ReferenceEquals(this, LastActivatedMaterial))
            return;

        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
        Metallic?.Deactivate();
        Roughness?.Deactivate();
        AO?.Deactivate();
        Emissive?.Deactivate();
        LastActivatedMaterial = null;
    }

    static Material LastActivatedMaterial;
}
