using System.Runtime.CompilerServices;

namespace BallisticEngine;

public readonly struct ProfileZone : IDisposable {
    readonly IProfilerBackend backend;
    readonly ulong handle;

    internal ProfileZone(IProfilerBackend backend, ulong handle) {
        this.backend = backend;
        this.handle = handle;
    }

    public void Dispose() => backend?.ZoneEnd(handle);
}
