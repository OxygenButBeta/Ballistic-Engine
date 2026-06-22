namespace BallisticEngine.AssetPipeline;

public static class AssetRef {
    const string GuidPrefix = "guid:";

    public static bool IsGuidRef(string reference, out Guid guid) {
        guid = Guid.Empty;
        return reference is not null
               && reference.StartsWith(GuidPrefix, StringComparison.OrdinalIgnoreCase)
               && Guid.TryParse(reference.AsSpan(GuidPrefix.Length), out guid);
    }

    public static string FromGuid(Guid guid) => GuidPrefix + guid.ToString("N");
}
