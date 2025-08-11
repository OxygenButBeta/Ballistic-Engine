using BallisticEngine;

public static class Graphics {
    public static StandardShader CreateStandardShader(string vertexCode, string fragmentCode) {
        if (SharedResources<Shader>.TryGetResource(ResourceIdentity.Combine(vertexCode, fragmentCode),
                out Shader cachedShader))
            return cachedShader as StandardShader;

        return new GLStandardShader(vertexCode, fragmentCode);
    }
}