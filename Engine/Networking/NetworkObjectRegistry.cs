namespace BallisticEngine;

internal sealed class NetworkObjectRegistry {
    const int SlotBits = 20;
    const int SlotMask = (1 << SlotBits) - 1;

    struct Slot {
        public NetworkObject Object;
        public int Generation;
    }

    Slot[] slots = new Slot[64];
    readonly Stack<int> freeSlots = new();
    int highWater = 1;
    int count;

    public int Count => count;

    public int Add(NetworkObject obj) {
        int slot = freeSlots.Count > 0 ? freeSlots.Pop() : NextHighWater();
        ref Slot s = ref slots[slot];
        s.Object = obj;
        if (s.Generation == 0)
            s.Generation = 1;
        count++;
        return Pack(slot, s.Generation);
    }

    public void AddWithId(int netId, NetworkObject obj) {
        int slot = netId & SlotMask;
        int generation = Generation(netId);
        if (slot <= 0)
            return;
        while (slot >= slots.Length)
            Array.Resize(ref slots, slots.Length * 2);
        if (slot >= highWater)
            highWater = slot + 1;
        ref Slot s = ref slots[slot];
        if (s.Object is null)
            count++;
        s.Object = obj;
        s.Generation = generation == 0 ? 1 : generation;
    }

    public void Remove(int netId) {
        int slot = netId & SlotMask;
        if (slot <= 0 || slot >= highWater)
            return;
        ref Slot s = ref slots[slot];
        if (s.Object is null || s.Generation != Generation(netId))
            return;
        s.Object = null;
        s.Generation++;
        if (s.Generation == 0) s.Generation = 1;
        freeSlots.Push(slot);
        count--;
    }

    public NetworkObject Resolve(int netId) {
        int slot = netId & SlotMask;
        if (slot <= 0 || slot >= highWater)
            return null;
        ref Slot s = ref slots[slot];
        return s.Generation == Generation(netId) ? s.Object : null;
    }

    public void Clear() {
        Array.Clear(slots);
        freeSlots.Clear();
        highWater = 1;
        count = 0;
    }

    public IEnumerable<NetworkObject> All() {
        for (int i = 1; i < highWater; i++)
            if (slots[i].Object is { } o)
                yield return o;
    }

    int NextHighWater() {
        if (highWater >= slots.Length)
            Array.Resize(ref slots, slots.Length * 2);
        return highWater++;
    }

    static int Pack(int slot, int generation) => (generation << SlotBits) | slot;
    static int Generation(int netId) => netId >> SlotBits;
}
