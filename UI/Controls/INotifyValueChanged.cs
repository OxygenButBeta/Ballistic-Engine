using System;

namespace BallisticEngine.UI;

// The value-bearing-control contract (UITK parity). Every control with a value (Toggle, Slider,
// TextField, Dropdown, ...) implements this so callers can read/write the value and subscribe to
// changes uniformly — and so data-binding (P7) can wire a control to a backing field generically.
public interface INotifyValueChanged<T>
{
    T Value { get; set; }

    // Fired AFTER the value changes (old, new). SetValueWithoutNotify changes it silently (to avoid
    // feedback loops when a binding pushes a value back into the control).
    event Action<T, T> ValueChanged;
    void SetValueWithoutNotify(T value);
}
