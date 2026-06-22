
namespace BallisticEngine;

public class ColorParameter(Vector3 value, bool hdr = false, bool overridden = false)
    : Vector3Parameter(value, overridden)
{
    public bool Hdr { get; } = hdr;
}
