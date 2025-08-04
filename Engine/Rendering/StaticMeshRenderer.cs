using OpenTK.Mathematics;

namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh Mesh { get; protected set; }
    public override Material Material { get; protected set; }

    protected internal override void OnBegin() {
        string baseDir = AppContext.BaseDirectory;  // veya Directory.GetCurrentDirectory();
        string modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck.fbx");

        Mesh = Mesh.ImportFromFile(modelPath);
        Texture2D defaultTexture = new();
        Shader defaultShader = new();
        Material = new Material(defaultTexture, defaultShader);
    }

    protected internal override void OnEnabled() {
        RenderTargets.Add(this);
    }

    protected internal override void OnDisabled() {
        RenderTargets.Remove(this);
    }

}