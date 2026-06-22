namespace BallisticEngine;

public abstract class DataAsset : BObject {
    protected internal virtual void OnLoaded() {
    }

    public static T CreateInstance<T>() where T : DataAsset, new() {
        var instance = new T();
        instance.OnLoaded();
        return instance;
    }

    public static DataAsset CreateInstance(Type type) {
        if (!typeof(DataAsset).IsAssignableFrom(type) || type.IsAbstract) {
            Debugging.LogError($"CreateInstance: {type?.Name} is not a concrete DataAsset.");
            return null;
        }
        var instance = (DataAsset)Activator.CreateInstance(type);
        instance.OnLoaded();
        return instance;
    }
}
