using System.Text.Json;

namespace BallisticEngine;

public static class SaveData {
    static string PathFor(string slot) =>
        Path.Combine(SaveSystem.SaveDirectory, SanitizeSlot(slot) + ".json");

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

    public static IEnumerable<string> AllSlots() {
        if (!Directory.Exists(SaveSystem.SaveDirectory))
            yield break;
        foreach (string file in Directory.EnumerateFiles(SaveSystem.SaveDirectory, "*.json")) {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name != "PlayerPrefs") yield return name;
        }
    }

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
