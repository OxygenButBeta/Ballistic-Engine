
namespace BallisticEngine;

public class EnumParameter<T>(T value, bool overridden = false) : VolumeParameter<T>(value, overridden), IEnumParameter
    where T : struct, Enum
{
    static readonly T[] Values = Enum.GetValues<T>();
    static readonly string[] ValueNames = Enum.GetNames<T>();

    public string[] Names => ValueNames;

    public int Index {
        get => Array.IndexOf(Values, value);
        set => this.value = Values[Math.Clamp(value, 0, Values.Length - 1)];
    }
}
