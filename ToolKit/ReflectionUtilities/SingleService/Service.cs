public static class Service<T> {
    static T instance;

    public static void Set(T value) => instance = value;
    public static bool IsSet() => instance != null;

    public static T Get() {
        if (instance == null)
            throw new InvalidOperationException($"Service<{typeof(T).Name}> is not set.");
        return instance;
    }
}