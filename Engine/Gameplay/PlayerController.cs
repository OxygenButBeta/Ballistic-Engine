using BallisticEngine.Networking;

namespace BallisticEngine;

[Component("Player Controller", "Gameplay")]
public class PlayerController : NetworkBehaviour {
    [NotSerialized]
    public Pawn Pawn { get; private set; }

    [NotSerialized]
    public InputComponent Input { get; private set; }

    public void Possess(Pawn pawn) {
        if (pawn is null)
            return;
        if (Pawn is not null)
            Unpossess();
        Pawn = pawn;
        pawn.FirePossessed(this);

        if (IsOwner)
            TrySetupInput();

        Network.Manager?.OnServerPossess(this, pawn);
    }

    public void Unpossess() {
        if (Pawn is null)
            return;
        Pawn p = Pawn;
        Pawn = null;
        p.FireUnpossessed();
        Network.Manager?.OnServerUnpossess(this);
    }

    protected internal override void OnStartLocalPlayer() => TrySetupInput();

    void TrySetupInput() {
        if (Input is not null)
            return;
        Input = CreateInputComponent();
        try { SetupInput(Input); }
        catch (Exception e) { ScriptGuard.Report(this, "SetupInput", e); }
    }

    protected virtual InputComponent CreateInputComponent() => new();

    protected virtual void SetupInput(InputComponent input) { }

    protected internal override void Tick(in float delta) {
        Input?.Sample(in delta);
    }

    [NotSerialized]
    public InputBuffer InputBuffer { get; private set; }

    [NotSerialized]
    public NetworkInput CurrentInput { get; private set; }

    internal NetworkInput CapturePredictionInput(uint seq) {
        InputBuffer ??= new InputBuffer();
        NetworkInput input = Input is not null ? Input.Capture(seq) : new NetworkInput(seq, 0f, 0f, 0u);
        InputBuffer.Push(in input);
        CurrentInput = input;
        return input;
    }

    internal void SetServerInput(NetworkInput input) => CurrentInput = input;

    internal void Reconcile(uint ackedSeq, Action<NetworkInput> replayTick) {
        if (InputBuffer is null)
            return;
        InputBuffer.AckThrough(ackedSeq);
        LastReplayCount = 0;
        foreach (NetworkInput input in InputBuffer.InOrder()) {
            CurrentInput = input;
            replayTick(input);
            LastReplayCount++;
        }
    }

    [NotSerialized]
    public int LastReplayCount { get; private set; }
}
