using System.Text.Json;

namespace BallisticEngine;

public static class SaveSystem {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public static string SaveDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Saves");

    public static void Initialize(string saveDirectory) {
        SaveDirectory = saveDirectory;
        Directory.CreateDirectory(saveDirectory);
        PlayerPrefs.Reset();
    }
}
