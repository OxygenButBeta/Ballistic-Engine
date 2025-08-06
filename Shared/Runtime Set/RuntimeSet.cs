public static class RuntimeSet<T> {
    static readonly HashSet<T> objects = new(capacity: 20);
    public static IReadOnlyCollection<T> ReadOnlyCollection => objects;

    public static event Action<T> OnAdded;
    public static event Action<T> OnRemoved;

    public static void Add(T obj) {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj), "Cannot add null object to RuntimeSet.");
        if (!objects.Add(obj))
            throw new InvalidOperationException("Object already exists in RuntimeSet.");
        OnAdded?.Invoke(obj);
    }

    public static void Remove(T obj) {
        if (objects.Remove(obj))
            OnRemoved?.Invoke(obj);
    }

    public static bool Contains(T obj) => objects.Contains(obj);

    public static void Clear() {
        foreach (T obj in objects)
            OnRemoved?.Invoke(obj);

        objects.Clear();
    }

    public static void ForceAdd(T obj) {
        Remove(obj);
        Add(obj);
    }
}