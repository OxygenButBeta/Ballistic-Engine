using System.Collections.Concurrent;
using Schedulers;

namespace BallisticEngine;

public interface IJobParallelFor {
    void Execute(int index);

    void Finish() { }

    int BatchSize => 64;
}
