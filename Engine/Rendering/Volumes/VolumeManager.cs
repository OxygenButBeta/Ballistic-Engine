
namespace BallisticEngine;

// Blends every active Volume in the scene into one VolumeStack (Unity's VolumeManager).
// Volumes register in OnAttach/OnDetach (so the editor sees them outside play mode); the
// renderer calls Update once per frame with the rendering camera's position, then reads the
// stack. Lower-priority volumes apply first so higher priorities win ties, and local volumes
// contribute by camera distance to their box (full inside, fading over BlendDistance outside).
public static class VolumeManager {
    static readonly List<Volume> volumes = new();
    static readonly List<Volume> sorted = new();
    static VolumeStack stack;

    // The highest-priority active volume whose profile carries an enabled GiVolume — its box bounds drive the DDGI
    // probe grid in Volume bounds mode. The blend stack can't carry geometry (it only blends component values), so
    // we capture the dominant GI volume here, the one place that knows which volume contributed which component.
    public static Volume DominantGiVolume { get; private set; }

    // Built lazily so ComponentRegistry.Build (bootstrap) has run and all component types exist.
    public static VolumeStack Stack =>
        stack ??= new VolumeStack(ComponentRegistry.VolumeMenu.Select(entry => entry.Type));

    // Drops the lazily-built stack so a script reload rebuilds it from the new component types —
    // the old stack would pin the unloaded assembly's VolumeComponent types forever.
    internal static void ResetStack() => stack = null;

    internal static void Register(Volume volume) {
        if (!volumes.Contains(volume))
            volumes.Add(volume);
    }

    internal static void Unregister(Volume volume) => volumes.Remove(volume);

    public static void Update(Vector3 cameraPosition) {
        VolumeStack target = Stack;
        target.Reset();
        DominantGiVolume = null;

        if (volumes.Count == 0)
            return;

        // Stable insertion sort by priority (List.Sort is unstable and would let equal-priority
        // volumes swap order — and therefore flicker — between frames). Volume counts are tiny.
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
                // Track the dominant GI volume (sorted ascending by priority → the last match wins = highest
                // priority active GI volume). Its box drives the probe grid in Volume bounds mode.
                if (component.Active && component is GiVolume g && g.enabled.Value)
                    DominantGiVolume = volume;
            }
        }
    }
}
