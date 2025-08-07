namespace BallisticEngine;

public class StaticMeshRenderer : Renderer
{
    public override Mesh CommonMesh { get; protected set; }
    public override Material Material { get; protected set; }

    protected internal override void OnBegin()
    {
        string baseDir = AppContext.BaseDirectory; // veya Directory.GetCurrentDirectory();
        string modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck.fbx");

        CommonMesh = Mesh.ImportFromFile(modelPath);

        Texture2D defaultTexture = RenderAsset.Current.CreateTexture2D(
            Path.Combine(baseDir, "Resources", "Default", "Texture.png"),
            TextureType.Diffuse);

        Shader defaultShader = new();
        Material = new Material(defaultTexture, defaultShader);
    }

    protected internal override void OnEnabled()
    {
        RuntimeSet<IRenderTarget>.Add(this);
    }

    protected internal override void OnDisabled()
    {
        RuntimeSet<IRenderTarget>.Remove(this);
    }
}