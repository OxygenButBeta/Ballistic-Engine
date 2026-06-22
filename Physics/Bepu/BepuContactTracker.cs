using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using static BallisticEngine.Bepu.BepuMath;
using NumVector3 = System.Numerics.Vector3;
using TkVector3 = System.Numerics.Vector3;

namespace BallisticEngine.Bepu;

sealed class BepuContactTracker {
    readonly BepuPhysicsWorld world;

    struct ContactRecord {
        public CollidableReference A, B;
        public int ChildA, ChildB;

        public NumVector3 Point;
        public NumVector3 Normal;
        public bool IsTrigger;
        public float Restitution;
    }

    struct TrackedPair {
        public BepuBody A, B;
        public int ChildA, ChildB;
        public TkVector3 Point, Normal;
        public bool IsTrigger;
    }

    readonly record struct PairKey(ulong Bodies, int ChildA, int ChildB) : IComparable<PairKey> {
        public int CompareTo(PairKey other) {
            int c = Bodies.CompareTo(other.Bodies);
            if (c != 0) return c;
            c = ChildA.CompareTo(other.ChildA);
            return c != 0 ? c : ChildB.CompareTo(other.ChildB);
        }
    }

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

    readonly Dictionary<ulong, ApproachSample> approachPeaks = new();

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
        float vn = NumVector3.Dot(relative, n);
        if (vn >= 0f)
            return;

        workerApproaches[workerIndex].Add(new ApproachSample {
            A = pair.A, B = pair.B, Point = point, Normal = normal, Speed = -vn,
        });
    }

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
            Restitution = 0f,
        });
    }

    static ulong KeyOf(CollidableReference a, CollidableReference b) =>
        a.Packed <= b.Packed
            ? ((ulong)a.Packed << 32) | b.Packed
            : ((ulong)b.Packed << 32) | a.Packed;

    static PairKey PairKeyOf(in ContactRecord r) =>
        r.A.Packed <= r.B.Packed
            ? new PairKey(KeyOf(r.A, r.B), r.ChildA, r.ChildB)
            : new PairKey(KeyOf(r.A, r.B), r.ChildB, r.ChildA);

    public void Flush() {
        events.Clear();
        if (pendingExits.Count > 0) {
            events.AddRange(pendingExits);
            pendingExits.Clear();
        }

        mergedPairs.Clear();
        foreach (List<ContactRecord> buffer in workerContacts) {
            foreach (ContactRecord record in buffer) mergedPairs.TryAdd(PairKeyOf(in record), record);
            buffer.Clear();
        }

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

            if (!known && !record.IsTrigger && record.Restitution > 0f &&
                approachPeaks.TryGetValue(key.Bodies, out ApproachSample peak)) {
                ApplyRestitutionImpulse(bodyA, bodyB, record.Restitution, in peak);
                approachPeaks.Remove(key.Bodies);
            }

            events.Add(MakeEvent(known ? PhysicsContactPhase.Stay : PhysicsContactPhase.Enter, in pair));
        }

        staleKeys.Clear();
        foreach ((PairKey key, TrackedPair pair) in trackedPairs) {
            if (mergedPairs.ContainsKey(key))
                continue;
            bool valid = pair.A.Valid && pair.B.Valid;
            bool dormant = valid && !pair.A.IsAwake && !pair.B.IsAwake;
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

        staleApproachKeys.Clear();
        foreach (ulong key in approachPeaks.Keys)
            if (!approachedThisStep.Contains(key))
                staleApproachKeys.Add(key);
        foreach (ulong key in staleApproachKeys)
            approachPeaks.Remove(key);
    }

    static void ApplyRestitutionImpulse(BepuBody a, BepuBody b, float restitution, in ApproachSample peak) {
        float targetSeparation = restitution * peak.Speed;
        if (targetSeparation <= 1e-3f)
            return;

        NumVector3 n = peak.Normal;
        float nLen = n.Length();
        if (nLen < 1e-6f)
            return;
        n /= nLen;

        float invMassSum = a.InverseMass + b.InverseMass;
        if (invMassSum <= 0f)
            return;

        NumVector3 point = peak.Point;
        NumVector3 relative = a.VelocityAt(in point) - b.VelocityAt(in point);
        float vn = NumVector3.Dot(relative, n);
        float deltaVn = targetSeparation - vn;
        if (deltaVn <= 0f)
            return;

        float j = deltaVn / invMassSum;
        NumVector3 impulse = n * j;

        a.ApplyRestitutionImpulse(in impulse, in point);
        NumVector3 negImpulse = -impulse;
        b.ApplyRestitutionImpulse(in negImpulse, in point);
    }

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
