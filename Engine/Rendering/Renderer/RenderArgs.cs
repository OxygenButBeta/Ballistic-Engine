namespace BallisticEngine;

public readonly struct RenderArgs {
  public  readonly IViewProjectionProvider viewProjectionProvider;

    public RenderArgs(IViewProjectionProvider viewProjectionProvider) {
        this.viewProjectionProvider = viewProjectionProvider;
    }
}