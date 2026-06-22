namespace BallisticEngine.UI;

public interface INotifyValueChanged<T>
{
    T Value { get; set; }

    event Action<T, T> ValueChanged;
    void SetValueWithoutNotify(T value);
}
