using System.Collections.Concurrent;
using Schedulers;

namespace BallisticEngine;

public interface IJob {
    void Execute();
}

public interface IJobParallelFor {
    void Execute(int index);

    void Finish() { }

    int BatchSize => 64;
}

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

public static class JobSystem {
    static JobScheduler scheduler;
    static readonly object initLock = new();

    static readonly ConcurrentStack<ActionForJob> forPool = new();

    public static bool IsInitialized => scheduler != null;

    public static int ThreadCount => Scheduler.ThreadCount;

    static JobScheduler Scheduler {
        get {
            if (scheduler != null)
                return scheduler;
            lock (initLock) {
                scheduler ??= new JobScheduler(new JobScheduler.Config {
                    ThreadPrefixName = "BallisticJobs",
                    ThreadCount = 0,
                    MaxExpectedConcurrentJobs = 128,
                    StrictAllocationMode = false,
                });
                return scheduler;
            }
        }
    }

    public static void Shutdown() {
        lock (initLock) {
            scheduler?.Dispose();
            scheduler = null;
        }
    }

    public static JobHandle Schedule(IJob job, JobHandle dependsOn = default) =>
        new(Scheduler.Schedule(new JobAdapter(job), dependsOn.Inner));

    public static JobHandle Schedule(IJobParallelFor job, int iterations, JobHandle dependsOn = default) =>
        new(Scheduler.Schedule(new ParallelForAdapter(job), iterations, dependsOn.Inner));

    public static JobHandle Combine(ReadOnlySpan<JobHandle> handles) {
        var raw = new Schedulers.JobHandle[handles.Length];
        for (var i = 0; i < handles.Length; i++)
            raw[i] = handles[i].Inner ?? throw new ArgumentException("Cannot combine a default JobHandle.");
        return new JobHandle(Scheduler.CombineDependencies(raw));
    }

    public static void Flush() => Scheduler.Flush();

    public static void CompleteAll(ReadOnlySpan<JobHandle> handles) {
        foreach (var handle in handles)
            handle.Complete();
    }

    public static void For(int iterations, Action<int> body, int batchSize = 64) {
        if (iterations <= 0)
            return;

        if (!forPool.TryPop(out var job))
            job = new ActionForJob();
        job.Body = body;
        job.Batch = batchSize;

        var handle = Scheduler.Schedule(job, iterations, null);
        Scheduler.Flush();
        handle.Complete();

        job.Body = null;
        forPool.Push(job);
    }

    sealed class JobAdapter(IJob job) : Schedulers.IJob {
        public void Execute() => job.Execute();
    }

    sealed class ParallelForAdapter(IJobParallelFor job) : Schedulers.IJobParallelFor {
        public int ThreadCount => 0;
        public int BatchSize => job.BatchSize;
        public void Execute(int index) => job.Execute(index);
        public void Finish() => job.Finish();
    }

    sealed class ActionForJob : Schedulers.IJobParallelFor {
        public Action<int> Body;
        public int Batch = 64;

        public int ThreadCount => 0;
        public int BatchSize => Batch;
        public void Execute(int index) => Body(index);
        public void Finish() { }
    }
}
