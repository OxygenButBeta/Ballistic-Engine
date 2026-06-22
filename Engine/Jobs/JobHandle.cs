using System.Collections.Concurrent;
using Schedulers;

namespace BallisticEngine;

public readonly struct JobHandle {
    readonly Schedulers.JobHandle inner;
    readonly bool valid;

    internal JobHandle(Schedulers.JobHandle inner) {
        this.inner = inner;
        valid = true;
    }

    internal Schedulers.JobHandle? Inner => valid ? inner : null;

    public void Complete() {
        if (valid)
            inner.Complete();
    }
}
