namespace BallisticEngine.Rendering;

//Just a text structure for the shaders
// TODO: Implement a shader loader
public static class DefaultShader {
    public static string VertexShader => GetVert();
    public static string FragmentShader => GetFrag();

    static string GetVert() {
        return GetGLSL("Vert.glsl");
    }


    static  string GetGLSL(string name) {
        return File.ReadAllText(AssetDatabase.GetAssetPath(name));
    }

    static string GetFrag(string filename = "DefaultShader") {
        return  GetGLSL("Frag.glsl");
    }
}