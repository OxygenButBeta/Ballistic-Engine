namespace BallisticEngine.Editor;

internal sealed class EditorPlayModeController {
    readonly System.Action saveBeforePlay;

    readonly System.Action onEntered;

    readonly System.Action onExited;

    public EditorPlayModeController(System.Action saveBeforePlay, System.Action onEntered, System.Action onExited) {
        this.saveBeforePlay = saveBeforePlay ?? throw new System.ArgumentNullException(nameof(saveBeforePlay));
        this.onEntered = onEntered ?? throw new System.ArgumentNullException(nameof(onEntered));
        this.onExited = onExited ?? throw new System.ArgumentNullException(nameof(onExited));
    }

    public bool IsPlaying => SceneManager.IsPlaying;

    public string BlockedReason => SceneManager.PlayBlocked?.Invoke();

    public bool CanEnterPlay => !SceneManager.IsPlaying && BlockedReason is null;

    public bool EnterPlay() {
        if (SceneManager.IsPlaying)
            return false;
        if (BlockedReason is not null)
            return false;
        saveBeforePlay();
        SceneManager.StartPlay();
        onEntered();
        return true;
    }

    public bool ExitPlay() {
        if (!SceneManager.IsPlaying)
            return false;
        SceneManager.StopPlay();
        onExited();
        return true;
    }
}
