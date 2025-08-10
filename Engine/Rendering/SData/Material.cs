using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject {
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Shader Shader { get; set; }

    Material(Texture2D diffuse, Shader shader) {
        Diffuse = diffuse;
        Shader = shader;
        Debugging.SystemLog("Material Created", SystemLogPriority.Critical);
    }

    public static Material Create(Shader shader, Texture2D diffuse, Texture2D normal) {
        if (RuntimeCache<(Guid, Guid, Guid), Material>.TryGetValue(
                (diffuse.InstanceId, shader.InstanceId, normal.InstanceId), out Material cachedMaterial)) {
            return cachedMaterial;
        }

        Material material = new(diffuse, shader);
        RuntimeCache<(Guid, Guid, Guid), Material>.Add((diffuse.InstanceId, shader.InstanceId, normal.InstanceId),
            material);
        return material;
    }

    public void Activate() {
        if (this.Equals(LastActivatedMaterial)) return; // Avoid reactivating the same material
        LastActivatedMaterial = this; // Update the last activated material
        Shader.Activate();
        Diffuse.Activate();
        Normal?.Activate();
    }

    public void Deactivate() {
        if (!LastActivatedMaterial.Equals(this)) return; // Avoid deactivating a material that wasn't activated
        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
    }

    static Material LastActivatedMaterial;
}