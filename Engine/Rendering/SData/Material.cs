using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject, ISharedResource
{
    public ResourceIdentity Identity { get; }
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Shader Shader { get; set; }

    Material(Shader shader, Texture2D diffuse, Texture2D normal)
    {
        Diffuse = diffuse;
        Normal = normal;
        Shader = shader;
    }

    public static Material Create(StandardShader standardShader, Texture2D diffuse, Texture2D normal = null)
    {
        return new Material(standardShader, diffuse, normal);
    }

    public void Activate()
    {
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        Shader.Activate();
        Diffuse.Activate();
        Normal?.Activate();
    }

    public void Deactivate()
    {
        if (!ReferenceEquals(this, LastActivatedMaterial))
            return;

        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
        LastActivatedMaterial = null;
    }

    static Material LastActivatedMaterial;
}