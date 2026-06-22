namespace BallisticEngine;

public class Skybox : SceneBehaviour {
    public static Skybox Active { get; private set; }

    public Texture3D Cubemap { get; set; }

    public float Exposure { get; set; } = 5000f;

    public Vector3 RotationEuler { get; set; }

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
