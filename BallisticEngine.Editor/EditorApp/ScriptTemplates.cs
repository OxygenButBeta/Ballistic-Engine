using System.Text;

namespace BallisticEngine.Editor;

internal static class ScriptTemplates {
    public static string ClassName(string fileStem) {
        var builder = new StringBuilder(fileStem.Length);
        foreach (var c in fileStem)
            if (char.IsLetterOrDigit(c) || c == '_')
                builder.Append(c);

        if (builder.Length == 0)
            return "NewScript";
        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }

    public static string Behaviour(string fileStem) =>
        $$"""
        using BallisticEngine;

        namespace Game;

        public class {{ClassName(fileStem)}} : Behaviour {
            protected override void OnBegin() {
            }

            protected override void Tick(in float delta) {
            }
        }

        """;

    public static void RewriteIfPristine(string oldStem, string newStem, string absolutePath) {
        try {
            if (File.ReadAllText(absolutePath) == Behaviour(oldStem))
                File.WriteAllText(absolutePath, Behaviour(newStem));
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Script rename: could not update class name: {exception.Message}");
        }
    }
}
