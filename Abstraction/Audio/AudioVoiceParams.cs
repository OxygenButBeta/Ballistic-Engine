
namespace BallisticEngine;

public struct AudioVoiceParams {
    public bool Spatial;
    public bool Looping;
    public float Volume;
    public float Pitch;
    public Vector3 Position;
    public Vector3 Velocity;
    public float MinDistance;
    public float MaxDistance;

    public static AudioVoiceParams Default => new() {
        Spatial = false,
        Looping = false,
        Volume = 1f,
        Pitch = 1f,
        MinDistance = 1f,
        MaxDistance = 500f,
    };
}
