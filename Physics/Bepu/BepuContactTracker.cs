using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using static BallisticEngine.Bepu.BepuMath;
using NumVector3 = System.Numerics.Vector3;
using TkVector3 = System.Numerics.Vector3;   // engine math is System.Numerics now (was OpenTK)

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
        public float Restitution;  // combined coefficient of restitution for this pair (0 = inelastic)
    }

    struct TrackedPair {
        public BepuBody A, B;
        public TkVector3 Point, Normal;
        public bool IsTrigger;
    }

    // One approach-speed sample taken inside narrowphase (possibly while still speculative), used to
    // capture the PEAK closing velocity before the solver bleeds it off. Merged per-pair in Flush.
    struct ApproachSample {
        public CollidableReference A, B;
        public NumVector3 Point, Normal;
        public float Speed;
    }

    readonly List<ContactRecord>[] workerContacts;
    readonly List<ApproachSample>[] workerApproaches;
    readonly Dictionary<ulong, TrackedPair> trackedPairs = new();
    readonly List<PhysicsContactEvent> events = new();
    readonly List<PhysicsContactEvent> pendingExits = new();

    // Per-pair peak approach speed (+ the contact point/normal of that peak), persisted across steps
    // until the pair either Enters (consumed) or stops sampling (cleared). Keyed like trackedPairs.
    readonly Dictionary<ulong, ApproachSample> approachPeaks = new();

    // Flush scratch, reused every step.
    readonly Dictionary<ulong, ContactRecord> mergedPairs = new();
    readonly List<ulong> sortedKeys = new();
    readonly List<ulong> staleKeys = new();
    readonly HashSet<ulong> approachedThisStep = new();
    readonly List<ulong> staleApproachKeys = new();

    public IReadOnlyList<PhysicsContactEvent> Events => events;

    public BepuContactTracker(BepuPhysicsWorld world, int threadCount) {
        this.world = world;
        int workers = Math.Max(1, threadCount);
        workerContacts = new List<ContactRecord>[workers];
        workerApproaches = new List<ApproachSample>[workers];
        for (var i = 0; i < workers; i++) {
            workerContacts[i] = new List<ContactRecord>(capacity: 32);
            workerApproaches[i] = new List<ApproachSample>(capacity: 32);
        }
    }

    // Called from narrowphase worker threads (lock-free, per-worker buffer). Samples the relative
    // normal approach speed of a restitution pair, even while the contact is still speculative, so
    // the peak impact velocity is captured before the solver damps it. Merged per-pair in Flush.
    public void SampleApproach(int workerIndex, CollidablePair pair, in NumVector3 offsetFromA,
        in NumVector3 normal, float restitution) {
        NumVector3 n = normal;
        float nLen = n.Length();
        if (nLen < 1e-6f)
            return;
        n /= nLen;

        NumVector3 point = world.GetPose(pair.A).Position + offsetFromA;
        BepuBody a = world.Lookup(pair.A);
        BepuBody b = world.Lookup(pair.B);
        if (a is null || b is null)
            return;

        NumVector3 relative = a.VelocityAt(in point) - b.VelocityAt(in point);
        float vn = NumVector3.Dot(relative, n); // <0 => approaching along +n
        if (vn >= 0f)
            return;

        workerApproaches[workerIndex].Add(new ApproachSample {
            A = pair.A, B = pair.B, Point = point, Normal = normal, Speed = -vn,
        });
    }

    // Called from narrowphase worker threads — each worker owns its buffer, no locks. Records a
    // TOUCHING contact (depth past the threshold) for Enter/Stay/Exit. The restitution approach
    // speed is tracked separately via SampleApproach (it must be captured pre-touch, before the
    // solver damps it), so this just stamps the combined restitution onto the record.
    public void Record(int workerIndex, CollidablePair pair, in NumVector3 offsetFromA,
        in NumVector3 normal, bool isTrigger, float restitution) {
        workerContacts[workerIndex].Add(new ContactRecord {
            A = pair.A,
            B = pair.B,
            Point = world.GetPose(pair.A).Position + offsetFromA,
            Normal = normal,
            IsTrigger = isTrigger,
            Restitution = restitution,
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

        // Merge this step's approach samples into the persistent per-pair peak (keep the highest
        // closing speed seen since the pair first appeared, including its speculative steps). A pair
        // that stops sampling but never Entered is cleared at the end of Flush (it separated again).
        approachedThisStep.Clear();
        foreach (List<ApproachSample> buffer in workerApproaches) {
            foreach (ApproachSample sample in buffer) {
                ulong key = KeyOf(sample.A, sample.B);
                approachedThisStep.Add(key);
                if (!approachPeaks.TryGetValue(key, out ApproachSample peak) || sample.Speed > peak.Speed)
                    approachPeaks[key] = sample;
            }
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

            // Real coefficient-of-restitution bounce: on the step a pair STARTS touching, top the
            // normal separation up to e·peakApproachSpeed (peak captured pre-touch, since speculative
            // contacts bleed the closing velocity off before depth crosses the touch threshold). The
            // contact spring is critically damped (no spring bounce), so this impulse is the entire
            // rebound — a true e²-energy restitution. Once only, on Enter: re-applying on Stay would
            // pump energy into a resting body.
            if (!known) {
                if (!record.IsTrigger && record.Restitution > 0f &&
                    approachPeaks.TryGetValue(key, out ApproachSample peak))
                    ApplyRestitutionImpulse(bodyA, bodyB, record.Restitution, in peak);
                approachPeaks.Remove(key); // consumed on Enter; a fresh approach re-seeds it
            }

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

        // Drop approach peaks that didn't sample this step and never Entered (the pair approached but
        // veered off before touching) so a stale peak can't fire a phantom bounce on a later contact.
        staleApproachKeys.Clear();
        foreach (ulong key in approachPeaks.Keys)
            if (!approachedThisStep.Contains(key))
                staleApproachKeys.Add(key);
        foreach (ulong key in staleApproachKeys)
            approachPeaks.Remove(key);
    }

    // Velocity-flip restitution at a contact point. Normal points from B toward A. peak.Speed is the
    // pre-touch peak closing speed; we top the pair up to a separation of e·peakSpeed along the
    // normal. The contact spring is critically damped (inelastic), so post-step the relative normal
    // velocity is ≈0 and this impulse IS the rebound. Statics contribute inverse mass 0, so a ball
    // hitting a wall keeps all the energy. Angular response comes free from point-impulse application.
    static void ApplyRestitutionImpulse(BepuBody a, BepuBody b, float restitution, in ApproachSample peak) {
        float targetSeparation = restitution * peak.Speed;
        if (targetSeparation <= 1e-3f)
            return; // glancing/resting contact — nothing meaningful to bounce

        NumVector3 n = peak.Normal;              // B -> A
        float nLen = n.Length();
        if (nLen < 1e-6f)
            return;
        n /= nLen;

        float invMassSum = a.InverseMass + b.InverseMass;
        if (invMassSum <= 0f)
            return; // two statics: nothing to move

        NumVector3 point = peak.Point;
        // Current post-step relative normal velocity (≈0 after the inelastic spring, but measure it
        // so we add exactly the deficit up to the target separation, never overshoot).
        NumVector3 relative = a.VelocityAt(in point) - b.VelocityAt(in point);
        float vn = NumVector3.Dot(relative, n);  // >0 already separating, <0 still closing
        float deltaVn = targetSeparation - vn;   // bring separation up to the target
        if (deltaVn <= 0f)
            return; // already separating faster than the target — don't add energy

        float j = deltaVn / invMassSum;          // scalar impulse magnitude (linear effective mass)
        NumVector3 impulse = n * j;

        a.ApplyRestitutionImpulse(in impulse, in point);
        NumVector3 negImpulse = -impulse;
        b.ApplyRestitutionImpulse(in negImpulse, in point);
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
        approachPeaks.Clear();
        events.Clear();
        pendingExits.Clear();
        foreach (List<ContactRecord> buffer in workerContacts)
            buffer.Clear();
        foreach (List<ApproachSample> buffer in workerApproaches)
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
