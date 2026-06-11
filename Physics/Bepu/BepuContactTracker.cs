using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using static BallisticEngine.Bepu.BepuMath;
using NumVector3 = System.Numerics.Vector3;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace BallisticEngine.Bepu;

// Turns Bepu's narrowphase manifolds into Enter/Stay/Exit contact events (Bepu itself has no
// event system — contacts are only visible inside ConfigureContactManifold, which runs on the
// narrowphase WORKER THREADS during Step). Recording is lock-free via per-worker buffers; the
// diff against the previous step's pair set runs single-threaded in Flush, right after the
// timestep. The engine's JobSystem is deliberately NOT used here: the parallel half already
// runs on Bepu's dispatcher, and the serial half must stay on the main thread because the
// engine dispatches the events into user code.
//
// Sleeping: when every body in a tracked pair sleeps, the narrowphase stops reporting it.
// That is NOT a separation — the pair goes dormant (no Stay, no Exit) and resumes Stay when
// woken, matching Unity. Removed bodies fire a deferred Exit on the next flush.
sealed class BepuContactTracker {
    readonly BepuPhysicsWorld world;

    struct ContactRecord {
        public CollidableReference A, B;
        public NumVector3 Point;   // world space, at narrowphase time
        public NumVector3 Normal;  // from B toward A (the abstraction contract)
        public bool IsTrigger;
    }

    struct TrackedPair {
        public BepuBody A, B;
        public TkVector3 Point, Normal;
        public bool IsTrigger;
    }

    readonly List<ContactRecord>[] workerContacts;
    readonly Dictionary<ulong, TrackedPair> trackedPairs = new();
    readonly List<PhysicsContactEvent> events = new();
    readonly List<PhysicsContactEvent> pendingExits = new();

    // Flush scratch, reused every step.
    readonly Dictionary<ulong, ContactRecord> mergedPairs = new();
    readonly List<ulong> sortedKeys = new();
    readonly List<ulong> staleKeys = new();

    public IReadOnlyList<PhysicsContactEvent> Events => events;

    public BepuContactTracker(BepuPhysicsWorld world, int threadCount) {
        this.world = world;
        workerContacts = new List<ContactRecord>[Math.Max(1, threadCount)];
        for (var i = 0; i < workerContacts.Length; i++)
            workerContacts[i] = new List<ContactRecord>(capacity: 32);
    }

    // Called from narrowphase worker threads — each worker owns its buffer, no locks.
    public void Record(int workerIndex, CollidablePair pair, in NumVector3 offsetFromA,
        in NumVector3 normal, bool isTrigger) {
        workerContacts[workerIndex].Add(new ContactRecord {
            A = pair.A,
            B = pair.B,
            Point = world.GetPose(pair.A).Position + offsetFromA,
            Normal = normal,
            IsTrigger = isTrigger,
        });
    }

    static ulong KeyOf(CollidableReference a, CollidableReference b) =>
        a.Packed <= b.Packed
            ? ((ulong)a.Packed << 32) | b.Packed
            : ((ulong)b.Packed << 32) | a.Packed;

    // Single-threaded, immediately after Simulation.Timestep: merge the worker buffers, diff
    // against the tracked set, and rewrite the event list for this step.
    public void Flush() {
        events.Clear();
        if (pendingExits.Count > 0) {
            events.AddRange(pendingExits);
            pendingExits.Clear();
        }

        mergedPairs.Clear();
        foreach (List<ContactRecord> buffer in workerContacts) {
            foreach (ContactRecord record in buffer)
                mergedPairs.TryAdd(KeyOf(record.A, record.B), record); // compounds: one event per body pair
            buffer.Clear();
        }

        // Deterministic event order regardless of worker scheduling.
        sortedKeys.Clear();
        sortedKeys.AddRange(mergedPairs.Keys);
        sortedKeys.Sort();

        foreach (ulong key in sortedKeys) {
            ContactRecord record = mergedPairs[key];
            BepuBody bodyA = world.Lookup(record.A);
            BepuBody bodyB = world.Lookup(record.B);
            if (bodyA is null || bodyB is null)
                continue;

            bool known = trackedPairs.ContainsKey(key);
            var pair = new TrackedPair {
                A = bodyA,
                B = bodyB,
                Point = ToOpenTK(record.Point),
                Normal = ToOpenTK(record.Normal),
                IsTrigger = record.IsTrigger,
            };
            trackedPairs[key] = pair;
            events.Add(MakeEvent(known ? PhysicsContactPhase.Stay : PhysicsContactPhase.Enter, in pair));
        }

        // Pairs that stopped reporting: separation (Exit) — unless every involved body is
        // static or asleep, in which case the contact persists and the pair just went dormant.
        staleKeys.Clear();
        foreach ((ulong key, TrackedPair pair) in trackedPairs) {
            if (mergedPairs.ContainsKey(key))
                continue;
            bool valid = pair.A.Valid && pair.B.Valid;
            bool dormant = valid && !pair.A.IsAwake && !pair.B.IsAwake; // IsAwake is false for statics
            if (dormant)
                continue;
            staleKeys.Add(key);
        }

        staleKeys.Sort();
        foreach (ulong key in staleKeys) {
            TrackedPair pair = trackedPairs[key];
            trackedPairs.Remove(key);
            events.Add(MakeEvent(PhysicsContactPhase.Exit, in pair));
        }
    }

    // A body is being removed mid-contact: queue Exits for its tracked pairs (delivered on the
    // next flush, with last-known contact data — Unity fires Exit on destroy too).
    public void OnBodyRemoved(BepuBody body) {
        staleKeys.Clear();
        foreach ((ulong key, TrackedPair pair) in trackedPairs)
            if (ReferenceEquals(pair.A, body) || ReferenceEquals(pair.B, body))
                staleKeys.Add(key);

        staleKeys.Sort();
        foreach (ulong key in staleKeys) {
            TrackedPair pair = trackedPairs[key];
            trackedPairs.Remove(key);
            pendingExits.Add(MakeEvent(PhysicsContactPhase.Exit, in pair));
        }
    }

    // World reset (leaving play mode): drop everything silently — components are torn down.
    public void Clear() {
        trackedPairs.Clear();
        events.Clear();
        pendingExits.Clear();
        foreach (List<ContactRecord> buffer in workerContacts)
            buffer.Clear();
    }

    static PhysicsContactEvent MakeEvent(PhysicsContactPhase phase, in TrackedPair pair) => new() {
        Phase = phase,
        A = pair.A,
        B = pair.B,
        Point = pair.Point,
        Normal = pair.Normal,
        IsTrigger = pair.IsTrigger,
    };
}
