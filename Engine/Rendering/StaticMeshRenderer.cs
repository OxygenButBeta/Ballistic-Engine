using BallisticEngine.Rendering;

namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; protected set; }
    public override Material SharedMaterial { get; protected set; }

    static int instanceCount = 0;

    protected internal override void OnBegin() {
        var baseDir = AppContext.BaseDirectory;
        var modelPath = Path.Combine(baseDir, "Resources", "Default", "PH.fbx");
        var diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH_DIFF.jpg");
        var normalPath = Path.Combine(baseDir, "Resources", "Default", "PH_NOR.png");

        instanceCount++;
        if (instanceCount % 2 != 0) {
             modelPath = Path.Combine(baseDir, "Resources", "Default", "PH.fbx");
             diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH_DIFF.jpg");
          //  normalPath = Path.Combine(baseDir, "Resources", "Default", "PH_NOR.png");
        }
        else  {
            modelPath = Path.Combine(baseDir, "Resources", "Default", "PH2.fbx");
            diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH2_DIFF.jpg");
           // normalPath = Path.Combine(baseDir, "Resources", "Default", "PH2_NOR.png");
        }

        SharedMesh = Mesh.ImportFromFile(modelPath);

        Texture2D defaultTexture = RenderAsset.Current.CreateTexture2D(diffusePath,
            TextureType.Diffuse);

        Texture2D normalTexture = RenderAsset.Current.CreateTexture2D(normalPath,
            TextureType.Normal);

        Shader standardShader = Graphics.CreateStandardShader(DefaultShader.VertexShader,
            DefaultShader.FragmentShader);

        SharedMaterial = Material.Create(standardShader, defaultTexture, normalTexture);
    }

    protected internal override void OnEnabled() {
        RuntimeSet<IStaticMeshRenderer>.Add(this);
    }

    protected internal override void OnDisabled() {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }
}