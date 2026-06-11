namespace BallisticEngine;

// Ballistic's ScriptableObject — a data-only asset that lives as a .asset file, edited in the
// inspector, and loaded by reference like any other asset. "DataAsset" (not "ScriptableObject")
// because it IS just authored data: no entity, no transform, no per-frame lifecycle. The principle
// is Unity's exactly — designers tweak balance/config/loot-table values in the editor, code reads
// them at runtime, and many components can share one instance.
//
// Authoring a data type is one subclass:
//
//     [CreateDataAsset(menu: "Weapons", fileName: "NewWeapon")]
//     public class WeaponData : DataAsset {
//         public string DisplayName = "Pistol";
//         public float Damage = 10f;
//         public Texture2D Icon;          // asset refs serialize as guids, like component fields
//     }
//
// Then: AssetDatabase.Load<WeaponData>("Assets/Weapons/Pistol.asset"). The same reflection rule as
// components decides what serializes (public read/write properties + public mutable fields, minus
// [NotSerialized]); asset-typed members round-trip as guid refs. Instances are GUID-cached, so two
// loads of the same path return the same object (shared data, Unity semantics).
//
// DataAsset types are discovered by ComponentRegistry at bootstrap/script-reload, so a new type
// appears in the editor's "Create > ..." menu with zero wiring, just like a component.
public abstract class DataAsset : BObject {
    // Optional hook: runs once right after the asset's serialized members are applied on load.
    // Override to derive cached/computed state from the authored values (Unity's OnEnable on an SO).
    protected internal virtual void OnLoaded() {
    }

    // Creates a runtime-only instance of a DataAsset type (Unity's ScriptableObject.CreateInstance).
    // Not backed by a file and not GUID-cached — for transient data built in code. Fires OnLoaded.
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
