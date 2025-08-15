
public static class SharedResources<T> where T : ISharedResource {
    static readonly Dictionary<ResourceIdentity, T> sharedResources = new();
    const bool EnableSharedResources = true;

    public static bool TryGetResource(ResourceIdentity identity, out T resource) {
        return sharedResources.TryGetValue(identity, out resource);
    }

    public static void AddResource(T resource) {
        if (!EnableSharedResources)
            return;
        ArgumentNullException.ThrowIfNull(resource);
        if (!sharedResources.TryAdd(resource.Identity, resource))
            throw new InvalidOperationException("Resource with the same identity already exists.");
    }

    public static void RemoveResource(ISharedResource sharedResource) {
        ArgumentNullException.ThrowIfNull(sharedResource);
        sharedResources.Remove(sharedResource.Identity);
    }
}