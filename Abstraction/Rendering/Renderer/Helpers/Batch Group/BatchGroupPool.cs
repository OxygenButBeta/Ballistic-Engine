namespace BallisticEngine.Rendering;
public static class BatchGroupPool<T> where T : IDrawable {
    static readonly Stack<BatchGroup<T>> pool = new();

    public static BatchGroup<T> Rent() {
        return pool.Count > 0 ? pool.Pop() : new BatchGroup<T>();
    }

    public static void Return(BatchGroup<T> group) {
        pool.Push(group);
    }
}