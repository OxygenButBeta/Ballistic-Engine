using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace BallisticEngine.AssetPipeline;

public readonly record struct ScriptDiagnostic(string File, int Line, int Column, bool IsError, string Code, string Message) {
    public override string ToString() => $"{File}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}
