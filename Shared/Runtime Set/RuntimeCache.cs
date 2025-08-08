// ReSharper disable HeuristicUnreachableCode

#pragma warning disable CS0162 // Unreachable code detected
namespace BallisticEngine.Shared.Runtime_Set;

public static class RuntimeCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    const bool DISABLE_CACHE = false;
    private static readonly Dictionary<TKey, TValue> cache = new();

    public static void Add(TKey key, TValue value)
    {
        if (DISABLE_CACHE) return;
        if (!cache.TryAdd(key, value))
        {
            return;
            throw new ArgumentException($"Key '{key}' already exists in the cache.");
        }
    }

    public static bool TryGetValue(TKey key, out TValue value)
    {
        if (!DISABLE_CACHE) return cache.TryGetValue(key, out value);
        value = null;
        return false;
    }

    public static bool ContainsKey(TKey key)
    {
        return !DISABLE_CACHE && cache.ContainsKey(key);
    }

    public static void Remove(TKey key)
    {
        if (!cache.Remove(key))
        {
            throw new KeyNotFoundException($"Key '{key}' not found in the cache.");
        }
    }

    public static void Clear()
    {
        cache.Clear();
    }
}