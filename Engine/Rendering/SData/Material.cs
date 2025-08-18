using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject, ISharedResource
{
    public ResourceIdentity Identity { get; }
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Texture2D Specular{ get; set; }
    public Texture2D Roughness{ get; set; }
    public Texture2D AO { get; set; }
    public Shader Shader { get; set; }


    Material(Shader shader, Texture2D diffuse, Texture2D normal,Texture2D specular, Texture2D roughness,Texture2D ao)
    {
        Diffuse = diffuse;
        Normal = normal;
        Shader = shader;
        Specular = specular;
        Roughness = roughness;
        AO = ao;
    }

    public static Material Create(StandardShader standardShader, Texture2D diffuse, Texture2D normal = null,Texture2D specular = null, Texture2D roughness = null,Texture2D ao = null)
    {
        return new Material(standardShader, diffuse, normal,specular , roughness,ao);
    }

    public void Activate()
    {
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        Shader.Activate();
        Diffuse.Activate();
        Specular?.Activate();
        Normal?.Activate();
        AO?.Activate();
        Roughness?.Activate();
    }

    public void Deactivate()
    {
        if (!ReferenceEquals(this, LastActivatedMaterial))
            return;

        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
        Specular?.Deactivate();
        Roughness?.Deactivate();
        AO?.Deactivate();
        LastActivatedMaterial = null;
    }

    static Material LastActivatedMaterial;
}