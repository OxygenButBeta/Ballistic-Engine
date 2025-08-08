namespace BallisticEngine;

public class StaticMeshRenderer : Renderer {
    public override Mesh SharedMesh { get; protected set; }
    public override Material SharedMaterial { get; protected set; }

    static int count = 0;

    protected internal override void OnBegin() {
        string baseDir = AppContext.BaseDirectory; // veya Directory.GetCurrentDirectory();
        string modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck.fbx");
        string texturePath = Path.Combine(baseDir, "Resources", "Default", "Texture.jpg");
        if (count % 2 == 0) {
            modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck2.fbx");
            texturePath = Path.Combine(baseDir, "Resources", "Default", "Texture2.jpg");
            modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck.fbx");
            texturePath = Path.Combine(baseDir, "Resources", "Default", "Texture.jpg");
        }
        else if (count % 3 == 0) {
            modelPath = Path.Combine(baseDir, "Resources", "Default", "Cat.fbx");
            texturePath = Path.Combine(baseDir, "Resources", "Default", "CatText.jpg");
        }
        else {
            modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck2.fbx");
            texturePath = Path.Combine(baseDir, "Resources", "Default", "Texture2.jpg");

        }
        modelPath = Path.Combine(baseDir, "Resources", "Default", "Duck2.fbx");
        texturePath = Path.Combine(baseDir, "Resources", "Default", "Texture2.jpg");
        count++;

        SharedMesh = Mesh.ImportFromFile(modelPath);

        Texture2D defaultTexture = RenderAsset.Current.CreateTexture2D(texturePath,
            TextureType.Diffuse);

        Shader defaultShader = Shader.CreateOrGetDefault();
        SharedMaterial = Material.Create(defaultTexture, defaultShader);
    }

    protected internal override void OnEnabled() {
        RuntimeSet<IOpaqueDrawable>.Add(this);
    }

    protected internal override void OnDisabled() {
        RuntimeSet<IOpaqueDrawable>.Remove(this);
    }
}