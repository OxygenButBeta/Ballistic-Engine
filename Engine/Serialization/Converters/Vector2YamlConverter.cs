using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BallisticEngine.Serialization;

public sealed class Vector2YamlConverter : IYamlTypeConverter {
    public bool Accepts(Type type) => type == typeof(Vector2);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) {
        parser.Consume<MappingStart>();
        var v = new Vector2();
        while (!parser.TryConsume<MappingEnd>(out _)) {
            var key = parser.Consume<Scalar>().Value;
            var value = float.Parse(parser.Consume<Scalar>().Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (key) { case "x": v.X = value; break; case "y": v.Y = value; break; }
        }
        return v;
    }

    public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer serializer) {
        var v = (Vector2)value;
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
        YamlScalar.Pair(emitter, "x", v.X); YamlScalar.Pair(emitter, "y", v.Y);
        emitter.Emit(new MappingEnd());
    }
}
