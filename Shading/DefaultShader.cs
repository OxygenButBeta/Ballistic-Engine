namespace BallisticEngine.Rendering;

//Just a text structure for the shaders
// TODO: Implement a shader loader
public static class DefaultShader {
    public static string VertexShader => GetVert();
    public static string FragmentShader => GetFrag();

    static string GetVert() {
        return GetGLSL("Vert");
    }


    static  string GetGLSL(string name) {
        var baseDir = AppContext.BaseDirectory;
        var shaderPath  = Path.Combine(baseDir, "Resources", "Default","Shaders", $"{name}.glsl");
        if (!File.Exists(shaderPath)) {
            throw new FileNotFoundException($"Shader file not found: {shaderPath}");
        }
        return File.ReadAllText(shaderPath);
    }

    static string GetFrag(string filename = "DefaultShader") {
        return  GetGLSL("Frag");
    }
}