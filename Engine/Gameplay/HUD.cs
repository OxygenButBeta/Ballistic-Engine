namespace BallisticEngine;

[Component("HUD", "Gameplay")]
public class HUD : SceneBehaviour {
    public static HUD Active { get; private set; }

    protected internal override void OnAttach() => Active = this;

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }

    public PlayerController Controller { get; private set; }
    public PlayerState PlayerState { get; private set; }

    internal void RunInit() {
        Controller = Network.LocalPlayerController;
        PlayerState = Network.LocalPlayerState;
        Init();
    }

    protected virtual void Init() { }
}
