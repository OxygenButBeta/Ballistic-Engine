public static class SharedResources<T> where T : ISharedResource
{
    static readonly Dictionary<ResourceIdentity, T> sharedResources = new();

    public static bool TryGetResource(ResourceIdentity identity, out T resource)
    {
        bool exists = sharedResources.TryGetValue(identity, out resource);
        if (exists)
        {
            Console.WriteLine("Resource found: " + identity + " of type " + typeof(T).Name);
        }
        else
        {
            Console.WriteLine("Resource not found: " + identity + " of type " + typeof(T).Name);
        }
        return exists;
    }

    public static void AddResource(T resource)
    {
        Console.WriteLine("Adding resource: " + resource.Identity + " of type " + typeof(T).Name);
        ArgumentNullException.ThrowIfNull(resource);
        if (!sharedResources.TryAdd(resource.Identity, resource))
            throw new InvalidOperationException("Resource with the same identity already exists.");
    }
}