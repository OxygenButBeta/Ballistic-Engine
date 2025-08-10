using BallisticEngine;

public class ResourceProviderFactory {
}

public class ResourceProviderHelper {
}

public interface IResourceProvider<in TProvider, TResourceMark> {
    TResourceMark ResourceMark { get; init; }
    bool IsEqualTo(TProvider provider);
}
public class ResourcePath : IResourceProvider<ResourcePath, string> {
    readonly int hash;
    public string ResourceMark { get; init; }

    public ResourcePath(string path) {
        hash = FNV1a.HashStr(path);
        ResourceMark = path;
    }

    public bool IsEqualTo(ResourcePath other) => other != null && hash == other.hash;
}

public readonly record struct ResourceIdentity {
    Guid Id { get; init; }

    public ResourceIdentity(Guid id) {
        Id = id;
    }
}

public interface ISharedResource<T> {
    ResourceIdentity Identity { get; }
}

public static class SharedResource<T> where T : BObject, ISharedResource<T> {
    static Dictionary<ResourceIdentity, T> sharedResources = new();
}