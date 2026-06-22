namespace BallisticEngine;

public sealed class OrderedPassList<TPass> {
    readonly List<TPass> registered = new();
    readonly Func<TPass, int> eventOf;
    TPass[] ordered = Array.Empty<TPass>();
    bool built;

    public OrderedPassList(Func<TPass, int> eventOf) =>
        this.eventOf = eventOf ?? throw new ArgumentNullException(nameof(eventOf));

    public OrderedPassList<TPass> Add(TPass pass) {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        registered.Add(pass);
        built = false;
        return this;
    }

    public void Build() {
        ordered = registered.OrderBy(eventOf).ToArray();
        built = true;
    }

    public IReadOnlyList<TPass> Passes { get { if (!built) Build(); return ordered; } }
    public int Count => registered.Count;

    public void Execute(Action<TPass> run) => Execute(run, int.MinValue, int.MaxValue);

    public void Execute(Action<TPass> run, int minEventInclusive, int maxEventExclusive) {
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (!built) Build();
        TPass[] list = ordered;
        for (int i = 0; i < list.Length; i++) {
            int ev = eventOf(list[i]);
            if (ev < minEventInclusive || ev >= maxEventExclusive) continue;
            run(list[i]);
        }
    }
}
