using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject, ISharedResource {
    public ResourceIdentity Identity { get; }
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Shader Shader { get; set; }

    Material(Texture2D diffuse, Shader shader, Texture2D normal) {
        Diffuse = diffuse;
        Normal = normal;
        Shader = shader;
        Identity = ResourceIdentity.Combine(diffuse.Identity, shader.Identity, normal.Identity);
        SharedResources<Material>.AddResource(this);
    }

    public static Material Create(Shader legacyShader, Texture2D diffuse, Texture2D normal = null) {
        
        ResourceIdentity normalIdentity = normal?.Identity ?? ResourceIdentity.Empty;

        ResourceIdentity materialIdentity = ResourceIdentity.Combine(diffuse.Identity, legacyShader.Identity, normalIdentity);
        
        return SharedResources<Material>.TryGetResource(materialIdentity, out Material sharedMaterial)
            ? sharedMaterial
            : new Material(diffuse, legacyShader, normal);
    }

    public void Activate() {
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        Shader.Activate();
        Diffuse.Activate();
        Normal?.Activate();
    }

    public void Deactivate() {
        if (!ReferenceEquals(this, LastActivatedMaterial))
            return;

        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
        LastActivatedMaterial = null;
    }

    static Material LastActivatedMaterial;
}