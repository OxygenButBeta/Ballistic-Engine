using System.Runtime.CompilerServices;

namespace BallisticEngine;

// Engine-wide profiling facade. Engine layers only ever talk to this BCL-only static class;
// a host exe may install a backend (Tracy) at startup. With no backend installed every call
// is a near-zero no-op, so instrumentation can stay in shipping code paths.
public interface IProfilerBackend {
    ulong ZoneBegin(string name, uint color, uint line, string file, string member);
    void ZoneEnd(ulong handle);
    void FrameMark();
    void Plot(string name, double value);
    void Message(string text);
}

public static class Profiler {
    public static IProfilerBackend Backend;

    // Time a scope: using (Profiler.Zone("Name")) { ... } or `using var _ = Profiler.Zone("Name");`
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

public readonly struct ProfileZone : IDisposable {
    readonly IProfilerBackend backend;
    readonly ulong handle;

    internal ProfileZone(IProfilerBackend backend, ulong handle) {
        this.backend = backend;
        this.handle = handle;
    }

    public void Dispose() => backend?.ZoneEnd(handle);
}
