public static class AssetDatabase {
    const string ResourcesDirectoryPath = @"..\..\..\Resources";
    static readonly Dictionary<string, string> assetPathMap = new();

    static AssetDatabase() {
        Directory.GetFiles(ResourcesDirectoryPath, "*", SearchOption.AllDirectories).ToList().ForEach(filePath => {
            var key = Path.GetFileName(filePath);
            assetPathMap[key] = Path.GetFullPath(filePath);
        });
    }

    public static string GetAssetPath(string assetName) {
        if (assetPathMap.TryGetValue(assetName, out var path)) {
            return path;
        }

        throw new FileNotFoundException($"Asset '{assetName}' not found in resources.");
    }
}