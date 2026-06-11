using System.Collections.Concurrent;
using Schedulers;

namespace BallisticEngine;

// Engine-owned job contracts. Systems implement these (never Schedulers.* directly) so the
// ZeroAllocJobScheduler package stays confined to this file and the backend can be swapped.
//
// RULES:
//  - Jobs run on worker threads: NO GL calls (the GL 3.3 context is main-thread only).
//    Jobs fill CPU buffers; the main thread uploads.
//  - Schedule/Flush/Complete from the main thread (Unity model). Execute bodies must not
//    touch scene state that the main thread mutates concurrently.

public interface IJob {
    void Execute();
}

public interface IJobParallelFor {
    void Execute(int index);

    // Called once on some worker after all iterations finish.
    void Finish() { }

    // Iterations handed to a worker at a time; tune upward for very cheap bodies.
    int BatchSize => 64;
}

// Deferred-completion ticket for a scheduled job. Complete() blocks until the job (and its
// whole dependency chain) finished. Default-constructed handles are inert no-ops.
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

// Frame-scoped parallelism over persistent worker threads (animation pose eval, culling,
// particle updates...). NOT for long-running background work — asset imports stay on
// Task.Run/Channels — and not for audio, which gets its own dedicated thread.
//
// Lazily initialized on first use. The workers are FOREGROUND threads: a host that ever
// touched the JobSystem MUST call JobSystem.Shutdown() after its window loop returns, or
// the process never exits.
public static class JobSystem {
    static JobScheduler scheduler;
    static readonly object initLock = new();

    // Pool for the Action<int> convenience wrapper. Safe to reuse because For() blocks
    // until the job completed before releasing the wrapper back.
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
                    ThreadCount = 0, // 0 = one per processor
                    MaxExpectedConcurrentJobs = 128,
                    StrictAllocationMode = false,
                });
                return scheduler;
            }
        }
    }

    // Idempotent; safe to call when never initialized. Hosts call this on exit (see class doc).
    public static void Shutdown() {
        lock (initLock) {
            scheduler?.Dispose();
            scheduler = null;
        }
    }

    // Queues a job (optionally after `dependsOn`). Nothing runs until Flush().
    public static JobHandle Schedule(IJob job, JobHandle dependsOn = default) =>
        new(Scheduler.Schedule(new JobAdapter(job), dependsOn.Inner));

    // Queues `iterations` calls of job.Execute(i) split across workers. Nothing runs until Flush().
    public static JobHandle Schedule(IJobParallelFor job, int iterations, JobHandle dependsOn = default) =>
        new(Scheduler.Schedule(new ParallelForAdapter(job), iterations, dependsOn.Inner));

    // Folds multiple handles into one (for fan-in dependency chains). Queued like a job: Flush() applies.
    public static JobHandle Combine(ReadOnlySpan<JobHandle> handles) {
        var raw = new Schedulers.JobHandle[handles.Length];
        for (var i = 0; i < handles.Length; i++)
            raw[i] = handles[i].Inner ?? throw new ArgumentException("Cannot combine a default JobHandle.");
        return new JobHandle(Scheduler.CombineDependencies(raw));
    }

    // Dispatches everything queued by Schedule() to the workers.
    public static void Flush() => Scheduler.Flush();

    public static void CompleteAll(ReadOnlySpan<JobHandle> handles) {
        foreach (var handle in handles)
            handle.Complete();
    }

    // Immediate-mode parallel for: schedules, flushes and blocks until done. The convenient
    // entry point for bulk array work inside a system's per-frame update.
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

    // Adapters bridging engine contracts to the package. Allocated per Schedule call — a few
    // gen0 bytes per job; pool them only if profiling ever shows it matters.
    sealed class JobAdapter(IJob job) : Schedulers.IJob {
        public void Execute() => job.Execute();
    }

    sealed class ParallelForAdapter(IJobParallelFor job) : Schedulers.IJobParallelFor {
        public int ThreadCount => 0; // 0 = use all workers
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
