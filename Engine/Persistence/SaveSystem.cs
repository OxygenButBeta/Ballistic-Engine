using System.Text.Json;

namespace BallisticEngine;

// Save-system roots: where persistent game data lives and the JSON dialect it uses. The save
// directory is injected at bootstrap (per-project, under the OS user-data folder — NOT the project
// source tree, so saves don't get committed). PlayerPrefs and SaveData<T> read it.
//
// Typed game saves go through SaveData<T>: any plain serializable class persists to a named slot.
//     SaveData.Save("slot1", playerState);
//     var state = SaveData.Load<PlayerState>("slot1");
public static class SaveSystem {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true, // game state is often public fields, not properties
    };

    // The absolute directory persistent saves are written to. Set by the bootstrap from the project
    // (e.g. %AppData%/Ballistic/<ProjectName>/Saves). Defaults to a local folder if never set so a
    // headless/test run still works.
    public static string SaveDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Saves");

    // Called by the bootstrap once the project is known. Re-points the directory and drops cached
    // PlayerPrefs so the next access reloads from the right place.
    public static void Initialize(string saveDirectory) {
        SaveDirectory = saveDirectory;
        Directory.CreateDirectory(saveDirectory);
        PlayerPrefs.Reset();
    }
}

// Typed save slots (Unity devs usually roll this by hand over JsonUtility; here it's built in). A
// "slot" is a named .json file under the save directory. T is any class/struct the JSON serializer
// can handle — public fields included (game state tends to use fields).
public static class SaveData {
    static string PathFor(string slot) =>
        Path.Combine(SaveSystem.SaveDirectory, SanitizeSlot(slot) + ".json");

    // Serializes `data` to the named slot, overwriting it. Returns false (logged) on I/O failure.
    public static bool Save<T>(string slot, T data) {
        try {
            Directory.CreateDirectory(SaveSystem.SaveDirectory);
            File.WriteAllText(PathFor(slot), JsonSerializer.Serialize(data, SaveSystem.JsonOptions));
            return true;
        }
        catch (Exception e) {
            Debugging.LogError($"SaveData.Save('{slot}') failed: {e.Message}");
            return false;
        }
    }

    // Loads the named slot, or returns `fallback` (default(T)) if it doesn't exist or fails to parse.
    public static T Load<T>(string slot, T fallback = default) {
        string path = PathFor(slot);
        if (!File.Exists(path))
            return fallback;
        try {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SaveSystem.JsonOptions) ?? fallback;
        }
        catch (Exception e) {
            Debugging.LogError($"SaveData.Load('{slot}') failed: {e.Message}");
            return fallback;
        }
    }

    public static bool Exists(string slot) => File.Exists(PathFor(slot));

    public static void Delete(string slot) {
        try {
            string path = PathFor(slot);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) {
            Debugging.LogError($"SaveData.Delete('{slot}') failed: {e.Message}");
        }
    }

    // Every existing slot name (file stem) under the save directory — for a "load game" screen.
    public static IEnumerable<string> AllSlots() {
        if (!Directory.Exists(SaveSystem.SaveDirectory))
            yield break;
        foreach (string file in Directory.EnumerateFiles(SaveSystem.SaveDirectory, "*.json")) {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name != "PlayerPrefs") // that's PlayerPrefs' backing file, not a game slot
                yield return name;
        }
    }

    // Slot names become file names — strip anything that isn't safe on disk.
    static string SanitizeSlot(string slot) {
        if (string.IsNullOrWhiteSpace(slot))
            return "default";
        Span<char> buffer = stackalloc char[slot.Length];
        int n = 0;
        foreach (char c in slot)
            buffer[n++] = Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c;
        return new string(buffer[..n]);
    }
}
