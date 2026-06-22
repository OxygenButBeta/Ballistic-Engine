namespace BallisticEngine.DX12;

internal sealed class Dx12FeatureIOBuilder : IFeatureIOBuilder {
    readonly Dx12PassBuilder builder;
    readonly string featureKey;
    int scratchCounter;

    public readonly List<string> Scratch = new();

    internal Dx12FeatureIOBuilder(Dx12PassBuilder builder, string featureKey) {
        this.builder = builder;
        this.featureKey = featureKey;
    }

    public void Read(string handleName) => builder.Read(builder.Resource(handleName));
    public void Write(string handleName) => builder.Write(builder.Resource(handleName));
    public void ReadWrite(string handleName) => builder.ReadWrite(builder.Resource(handleName));

    public string RequestScratch(string roleName) {
        string name = $"Feature.{featureKey}.{roleName}.{scratchCounter++}";
        Scratch.Add(name);
        builder.Resource(name, imported: false);
        return name;
    }

    public void AllowCulling(bool allow = true) {
        if (allow) builder.AllowCulling();
    }
}
