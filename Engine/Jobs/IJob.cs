using System.Collections.Concurrent;
using Schedulers;

namespace BallisticEngine;

public interface IJob {
    void Execute();
}
