using System.Collections;

namespace BallisticEngine.UI;

public sealed class ObservableList<T> : IList<T>, IList
{
    readonly List<T> _items = new();
    public event Action Changed;

    void Raise() => Changed?.Invoke();

    public T this[int index]
    {
        get => _items[index];
        set { _items[index] = value; Raise(); }
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    public void Add(T item) { _items.Add(item); Raise(); }
    public void AddRange(IEnumerable<T> items) { _items.AddRange(items); Raise(); }
    public void Insert(int index, T item) { _items.Insert(index, item); Raise(); }
    public bool Remove(T item) { bool r = _items.Remove(item); if (r) Raise(); return r; }
    public void RemoveAt(int index) { _items.RemoveAt(index); Raise(); }
    public void Clear() { _items.Clear(); Raise(); }
    public bool Contains(T item) => _items.Contains(item);
    public int IndexOf(T item) => _items.IndexOf(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    object IList.this[int index] { get => _items[index]; set { _items[index] = (T)value; Raise(); } }
    bool IList.IsFixedSize => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object value) { Add((T)value); return _items.Count - 1; }
    bool IList.Contains(object value) => value is T t && _items.Contains(t);
    int IList.IndexOf(object value) => value is T t ? _items.IndexOf(t) : -1;
    void IList.Insert(int index, object value) => Insert(index, (T)value);
    void IList.Remove(object value) { if (value is T t) Remove(t); }
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
