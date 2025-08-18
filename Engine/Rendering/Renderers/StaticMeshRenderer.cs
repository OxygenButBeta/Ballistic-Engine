using BallisticEngine.Rendering;

namespace BallisticEngine;

public class StaticMeshRenderer : Renderer
{
    public override Mesh SharedMesh { get; protected set; }
    public override Material SharedMaterial { get; protected set; }

 public   static int instanceCount = 0;

    protected internal override void OnBegin()
    {
        var baseDir = AppContext.BaseDirectory;
        string modelPath;
        string normalPath;
        string diffusePath;
        string metallicPath;
        string roughnessPath;
        string aoPath;
        switch (instanceCount)
        {
            case 0:
                modelPath = Path.Combine(baseDir, "Resources", "Default", "PH.fbx");
                diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH_DIFF.jpg");
                normalPath = Path.Combine(baseDir, "Resources", "Default", "PH_NOR.png");
                break;
            case 1:
                modelPath = Path.Combine(baseDir, "Resources", "Default", "PH3.fbx");
                diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH3_DIFF.jpg");
                normalPath = Path.Combine(baseDir, "Resources", "Default", "PH3_NOR.png");

                break;
            case 2:
                modelPath = Path.Combine(baseDir, "Resources", "Default", "PH2.fbx");
                diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH2_DIFF.jpg");
                normalPath = Path.Combine(baseDir, "Resources", "Default", "PH2_NOR.png");

                break;
            default:
                // fallback, shouldn't happen
                modelPath = Path.Combine(baseDir, "Resources", "Default", "PH.fbx");
                diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH_DIFF.jpg");
                normalPath = Path.Combine(baseDir, "Resources", "Default", "PH_NOR.png");

                break;
        }
        modelPath = Path.Combine(baseDir, "Resources", "Default", "PH7.fbx");
        diffusePath = Path.Combine(baseDir, "Resources", "Default", "PH7_DIFF.png");
        normalPath = Path.Combine(baseDir, "Resources", "Default", "PH7_NOR.png");
        metallicPath = Path.Combine(baseDir, "Resources", "Default", "PH7_METAL.png");
        roughnessPath = Path.Combine(baseDir, "Resources", "Default", "PH7_ROUGH.png");
         aoPath = Path.Combine(baseDir, "Resources", "Default", "PH7_AO.png");
        
        instanceCount = (instanceCount + 1) % 3;
        SharedMesh = Mesh.ImportFromFile(modelPath);

        StandardShader standardShader = Graphics.CreateStandardShader(DefaultShader.VertexShader,
            DefaultShader.FragmentShader);
        
        Texture2D defaultTexture = RenderAsset.Current.CreateTexture2D(diffusePath,
            TextureType.Diffuse);
        
        Texture2D metallicTexture = RenderAsset.Current.CreateTexture2D(metallicPath,
            TextureType.Metallic);
        
        Texture2D normal = RenderAsset.Current.CreateTexture2D(normalPath,
            TextureType.Normal);
        
        Texture2D roughness = RenderAsset.Current.CreateTexture2D(roughnessPath,
            TextureType.Roughness);
        
        Texture2D ao = RenderAsset.Current.CreateTexture2D(aoPath,
            TextureType.AO);
        

 
        SharedMaterial = Material.Create(standardShader, defaultTexture, normal,metallicTexture,roughness,ao:ao);
    }

    protected internal override void OnEnabled()
    {
        RuntimeSet<IStaticMeshRenderer>.Add(this);
    }

    protected internal override void OnDisabled()
    {
        RuntimeSet<IStaticMeshRenderer>.Remove(this);
    }
}