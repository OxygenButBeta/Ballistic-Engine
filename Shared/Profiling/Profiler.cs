using System.Runtime.CompilerServices;

namespace BallisticEngine;

public static class Profiler {
    public static IProfilerBackend Backend;

    public static ProfileZone Zone(
        string name = null,
        uint color = 0,
        [CallerLineNumber] int line = 0,
        [CallerFilePath] string file = null,
        [CallerMemberName] string member = null) {
        var backend = Backend;
        return backend is null
            ? default
            : new ProfileZone(backend, backend.ZoneBegin(name, color, (uint)line, file, member));
    }

    public static void FrameMark() => Backend?.FrameMark();
    public static void Plot(string name, double value) => Backend?.Plot(name, value);
    public static void Message(string text) => Backend?.Message(text);
}
