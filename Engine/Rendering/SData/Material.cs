using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject, ISharedResource
{
    public ResourceIdentity Identity { get; }
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public LegacyShader LegacyShader { get; set; }

    Material(Texture2D diffuse, LegacyShader legacyShader, Texture2D normal)
    {
        Diffuse = diffuse;
        Normal = normal;
        LegacyShader = legacyShader;
        Identity = ResourceIdentity.Combine(diffuse.Identity, legacyShader.Identity, normal.Identity);
        SharedResources<Material>.AddResource(this);
    }

    public static Material Create(LegacyShader legacyShader, Texture2D diffuse, Texture2D normal)
    {
        ResourceIdentity materialIdentity =
            ResourceIdentity.Combine(diffuse.Identity, legacyShader.Identity, normal.Identity);
        return SharedResources<Material>.TryGetResource(materialIdentity, out Material sharedMaterial)
            ? sharedMaterial
            : new Material(diffuse, legacyShader, normal);
    }

    public void Activate()
    {
        if (this.Equals(LastActivatedMaterial)) return; // Avoid reactivating the same material
        LastActivatedMaterial = this; // Update the last activated material
        LegacyShader.Activate();
        Diffuse.Activate();
        Normal.Activate();
    }

    public void Deactivate()
    {
        if (!LastActivatedMaterial.Equals(this)) return; // Avoid deactivating a material that wasn't activated
        LegacyShader.Deactivate();
        Diffuse.Deactivate();
        Normal.Deactivate();
    }

    static Material LastActivatedMaterial;
}