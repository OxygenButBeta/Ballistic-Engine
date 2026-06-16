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
        public int ChildA, ChildB; // compound child indices (0 for single-shape); distinguish a
                                    // body pair's trigger sub-contact from its solid one
        public NumVector3 Point;   // world space, at narrowphase time
        public NumVector3 Normal;  // from B toward A (the abstraction contract)
        public bool IsTrigger;
        public float Restitution;  // combined coefficient of restitution for this pair (0 = inelastic)
    }

    struct TrackedPair {
        public BepuBody A, B;
        public int ChildA, ChildB; // -1 when unknown (solid top-level path); real index for triggers
        public TkVector3 Point, Normal;
        public bool IsTrigger;
    }

    // Event key: a body pair PLUS the child sub-pair, so one Rigidbody with a solid child and a
    // trigger child reports both separately instead of one masking the other. Solid (whole-pair)
    // contacts use child indices (0,0)-style from the top-level path; per-child trigger contacts use
    // the real compound child indices. Ordered to match KeyOf's handle ordering for determinism.
    // IComparable so Flush can sort keys into a deterministic, worker-schedule-independent event order.
    readonly record struct PairKey(ulong Bodies, int ChildA, int ChildB) : IComparable<PairKey> {
        public int CompareTo(PairKey other) {
            int c = Bodies.CompareTo(other.Bodies);
            if (c != 0) return c;
            c = ChildA.CompareTo(other.ChildA);
            return c != 0 ? c : ChildB.CompareTo(other.ChildB);
        }
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
    readonly Dictionary<PairKey, TrackedPair> trackedPairs = new();
    readonly List<PhysicsContactEvent> events = new();
    readonly List<PhysicsContactEvent> pendingExits = new();

    // Per-BODY-pair peak approach speed (restitution is a whole-body property, no child split needed).
    // Persisted across steps until the pair Enters (consumed) or stops sampling (cleared).
    readonly Dictionary<ulong, ApproachSample> approachPeaks = new();

    // Flush scratch, reused every step.
    readonly Dictionary<PairKey, ContactRecord> mergedPairs = new();
    readonly List<PairKey> sortedKeys = new();
    readonly List<PairKey> staleKeys = new();
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
    // solver damps it), so this just stamps the combined restitution onto the record. childA/childB
    // default to -1 ("which child unknown"): the top-level solid path cannot reliably decode the
    // source child from Bepu's reduced manifold, so solid compound contacts resolve to the body's
    // primary collider. Trigger contacts come through RecordChild with the real indices.
    public void Record(int workerIndex, CollidablePair pair, in NumVector3 offsetFromA,
        in NumVector3 normal, bool isTrigger, float restitution, int childA = -1, int childB = -1) {
        workerContacts[workerIndex].Add(new ContactRecord {
            A = pair.A,
            B = pair.B,
            ChildA = childA,
            ChildB = childB,
            Point = world.GetPose(pair.A).Position + offsetFromA,
            Normal = normal,
            IsTrigger = isTrigger,
            Restitution = restitution,
        });
    }

    // Per-child overload: a single compound child touched another collidable. Used by the per-child
    // narrowphase to report a trigger child's overlap as its OWN event (distinct from a solid
    // sibling's). The contact point/normal come straight from the per-child convex manifold.
    public void RecordChild(int workerIndex, CollidablePair pair, int childA, int childB,
        in NumVector3 worldPoint, in NumVector3 normal, bool isTrigger) {
        workerContacts[workerIndex].Add(new ContactRecord {
            A = pair.A,
            B = pair.B,
            ChildA = childA,
            ChildB = childB,
            Point = worldPoint,
            Normal = normal,
            IsTrigger = isTrigger,
            Restitution = 0f, // triggers never bounce
        });
    }

    static ulong KeyOf(CollidableReference a, CollidableReference b) =>
        a.Packed <= b.Packed
            ? ((ulong)a.Packed << 32) | b.Packed
            : ((ulong)b.Packed << 32) | a.Packed;

    // Full event key: body pair + child sub-pair, child indices ordered to match the handle ordering
    // so the same physical sub-contact always hashes identically regardless of A/B argument order.
    static PairKey PairKeyOf(in ContactRecord r) =>
        r.A.Packed <= r.B.Packed
            ? new PairKey(KeyOf(r.A, r.B), r.ChildA, r.ChildB)
            : new PairKey(KeyOf(r.A, r.B), r.ChildB, r.ChildA);

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
                // One event per (body pair, child sub-pair): a solid child and a trigger child of the
                // same body pair are distinct keys, so neither masks the other.
                mergedPairs.TryAdd(PairKeyOf(in record), record);
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

        foreach (PairKey key in sortedKeys) {
            ContactRecord record = mergedPairs[key];
            BepuBody bodyA = world.Lookup(record.A);
            BepuBody bodyB = world.Lookup(record.B);
            if (bodyA is null || bodyB is null)
                continue;

            bool known = trackedPairs.ContainsKey(key);
            var pair = new TrackedPair {
                A = bodyA,
                B = bodyB,
                ChildA = record.ChildA,
                ChildB = record.ChildB,
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
            // pump energy into a resting body. Restitution is a whole-BODY property, so peaks key by
            // the body pair (key.Bodies), not the child sub-pair.
            if (!known && !record.IsTrigger && record.Restitution > 0f &&
                approachPeaks.TryGetValue(key.Bodies, out ApproachSample peak)) {
                ApplyRestitutionImpulse(bodyA, bodyB, record.Restitution, in peak);
                approachPeaks.Remove(key.Bodies); // consumed on Enter; a fresh approach re-seeds it
            }

            events.Add(MakeEvent(known ? PhysicsContactPhase.Stay : PhysicsContactPhase.Enter, in pair));
        }

        // Pairs that stopped reporting: separation (Exit) — unless every involved body is
        // static or asleep, in which case the contact persists and the pair just went dormant.
        staleKeys.Clear();
        foreach ((PairKey key, TrackedPair pair) in trackedPairs) {
            if (mergedPairs.ContainsKey(key))
                continue;
            bool valid = pair.A.Valid && pair.B.Valid;
            bool dormant = valid && !pair.A.IsAwake && !pair.B.IsAwake; // IsAwake is false for statics
            if (dormant)
                continue;
            staleKeys.Add(key);
        }

        staleKeys.Sort();
        foreach (PairKey key in staleKeys) {
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
        foreach ((PairKey key, TrackedPair pair) in trackedPairs)
            if (ReferenceEquals(pair.A, body) || ReferenceEquals(pair.B, body))
                staleKeys.Add(key);

        staleKeys.Sort();
        foreach (PairKey key in staleKeys) {
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
        ChildA = pair.ChildA,
        ChildB = pair.ChildB,
    };
}
