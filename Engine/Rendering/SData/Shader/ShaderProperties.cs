using System.Collections;

namespace BallisticEngine;

// An ordered, immutable collection of a shader's declared properties with O(1) lookup by name and
// by semantic. Order is preserved because the editor renders properties top-to-bottom in declared
// order, and the renderer binds texture slots in declared order (DiffuseMap -> t0, ... ).
public sealed class ShaderProperties : IReadOnlyList<ShaderProperty> {
    public static readonly ShaderProperties Empty = new([]);

    readonly ShaderProperty[] properties;
    readonly Dictionary<string, ShaderProperty> byName;
    readonly Dictionary<MaterialSemantic, ShaderProperty> bySemantic;

    public ShaderProperties(IReadOnlyList<ShaderProperty> declared) {
        properties = declared is ShaderProperty[] arr ? arr : [.. declared];
        byName = new Dictionary<string, ShaderProperty>(properties.Length, StringComparer.Ordinal);
        bySemantic = new Dictionary<MaterialSemantic, ShaderProperty>(properties.Length);
        foreach (var p in properties) {
            byName[p.Name] = p;
            // First declaration of a semantic wins; None can repeat (custom props) so it is not indexed.
            if (p.Semantic != MaterialSemantic.None)
                bySemantic.TryAdd(p.Semantic, p);
        }
    }

    public int Count => properties.Length;
    public ShaderProperty this[int index] => properties[index];

    public ShaderProperty ByName(string name) =>
        name is not null && byName.TryGetValue(name, out var p) ? p : null;

    public ShaderProperty BySemantic(MaterialSemantic semantic) =>
        bySemantic.TryGetValue(semantic, out var p) ? p : null;

    public IEnumerator<ShaderProperty> GetEnumerator() => ((IEnumerable<ShaderProperty>)properties).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => properties.GetEnumerator();
}
