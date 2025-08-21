public static class Resources {
    public static string GetResourcePath(string resourceName) {
        return Path.Combine(AppContext.BaseDirectory, "Resources", "Default", resourceName);
    }

    public static string ReadResourceText(string resourceName) {
        string path = GetResourcePath(resourceName);
        if (!File.Exists(path)) {
            throw new FileNotFoundException($"Resource file not found at path: {path}");
        }

        return File.ReadAllText(path);
    }
}