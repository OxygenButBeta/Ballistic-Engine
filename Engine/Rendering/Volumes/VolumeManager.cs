
namespace BallisticEngine;

public static class VolumeManager {
    static readonly List<Volume> volumes = new();
    static readonly List<Volume> sorted = new();
    static VolumeStack stack;

    public static VolumeStack Stack =>
        stack ??= new VolumeStack(ComponentRegistry.VolumeMenu.Select(entry => entry.Type));

    internal static void ResetStack() => stack = null;

    internal static void Register(Volume volume) {
        if (!volumes.Contains(volume))
            volumes.Add(volume);
    }

    internal static void Unregister(Volume volume) => volumes.Remove(volume);

    public static void Update(Vector3 cameraPosition) {
        VolumeStack target = Stack;
        target.Reset();

        if (volumes.Count == 0)
            return;

        sorted.Clear();
        sorted.AddRange(volumes);
        for (var i = 1; i < sorted.Count; i++) {
            Volume current = sorted[i];
            int j = i - 1;
            while (j >= 0 && sorted[j].Priority > current.Priority) {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = current;
        }

        foreach (Volume volume in sorted) {
            if (!volume.IsActive || volume.Profile is null)
                continue;

            float interp = volume.ComputeInterpFactor(cameraPosition) * Math.Clamp(volume.Weight, 0f, 1f);
            if (interp <= 0f)
                continue;

            foreach (VolumeComponent component in volume.Profile.Components) {
                if (component.Active)
                    target.Get(component.GetType())?.Override(component, interp);
            }
        }
    }
}
