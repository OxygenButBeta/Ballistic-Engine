using BallisticEngine.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BallisticEngine;

public static class SceneYaml {
    public static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new Vector2YamlConverter())
        .WithTypeConverter(new Vector3YamlConverter())
        .WithTypeConverter(new QuaternionYamlConverter())
        .Build();

    public static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new Vector2YamlConverter())
        .WithTypeConverter(new Vector3YamlConverter())
        .WithTypeConverter(new QuaternionYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();
}
