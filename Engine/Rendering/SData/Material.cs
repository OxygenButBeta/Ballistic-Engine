using BallisticEngine.Shared.Runtime_Set;

namespace BallisticEngine;

public class Material : BObject {
    public Texture2D Texture { get; set; }
    public Shader Shader { get; set; }

    Material(Texture2D texture, Shader shader) {
        Texture = texture;
        Shader = shader;
        Debugging.SystemLog("Material Created", SystemLogPriority.Critical);
    }

    public static Material Create(Texture2D texture, Shader shader) {
        if (RuntimeCache<(Guid, Guid), Material>.TryGetValue((texture.InstanceId, shader.InstanceId),
                out Material cachedMaterial)) {
            return cachedMaterial;
        }

        Material material = new(texture, shader);
        RuntimeCache<(Guid, Guid), Material>.Add((texture.InstanceId, shader.InstanceId), material);
        return material;
    }
}