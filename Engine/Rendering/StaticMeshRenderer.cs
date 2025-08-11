using BallisticEngine.Rendering;

namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; protected set; }
    public override Material SharedMaterial { get; protected set; }


    protected internal override void OnBegin() {
        var baseDir = AppContext.BaseDirectory; // veya Directory.GetCurrentDirectory();
        var modelPath = Path.Combine(baseDir, "Resources", "Default", "PH.fbx");
        var diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH_DIFF.jpg");
        var normalPath = Path.Combine(baseDir, "Resources", "Default", "PH_NOR.png");

        SharedMesh = Mesh.ImportFromFile(modelPath);

        Texture2D defaultTexture = RenderAsset.Current.CreateTexture2D(diffusePath,
            TextureType.Diffuse);
        
        Texture2D normalTexture = RenderAsset.Current.CreateTexture2D(normalPath,
            TextureType.Normal);

        Shader standardShader = Graphics.CreateStandardShader(DefaultShader.VertexShader,
            DefaultShader.FragmentShader);
        
        SharedMaterial = Material.Create(standardShader,defaultTexture,normalTexture);
    }

    protected internal override void OnEnabled() {
        RuntimeSet<IOpaqueDrawable>.Add(this);
    }

    protected internal override void OnDisabled() {
        RuntimeSet<IOpaqueDrawable>.Remove(this);
    }
}