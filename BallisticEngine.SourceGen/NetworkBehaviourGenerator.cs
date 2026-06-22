using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BallisticEngine.SourceGen;

[Generator(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class NetworkBehaviourGenerator : IIncrementalGenerator {
    const string NetworkBehaviourFullName = "BallisticEngine.NetworkBehaviour";
    const string IReplicatedFullName = "BallisticEngine.Networking.IReplicated";
    const string NetworkedAttr = "BallisticEngine.NetworkedAttribute";
    const string RpcAttr = "BallisticEngine.RpcAttribute";

    const int MaxNetworkedFields = 32;
    static readonly DiagnosticDescriptor TooManyNetworkedFields = new(
        id: "BNET001",
        title: "Too many [Networked] fields",
        messageFormat: "'{0}' declares {1} [Networked] fields; the replication changemask supports at most "
            + MaxNetworkedFields + ". Split the replicated state across multiple NetworkBehaviours.",
        category: "BallisticEngine.Networking",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax c && c.BaseList is not null,
            transform: static (ctx, _) => GetTarget(ctx))
            .Where(static t => t is not null);

        context.RegisterSourceOutput(candidates, static (spc, target) => Emit(spc, target!));
    }

    static NetTarget? GetTarget(GeneratorSyntaxContext ctx) {
        var decl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol)
            return null;
        bool isNetworkBehaviour = DerivesFromNetworkBehaviour(symbol);
        bool isSceneReplicated = !isNetworkBehaviour && ImplementsIReplicated(symbol);
        if (!isNetworkBehaviour && !isSceneReplicated)
            return null;

        var fields = new List<NetField>();
        var rpcs = new List<NetRpc>();

        foreach (ISymbol member in symbol.GetMembers()) {
            if (member is IPropertySymbol prop && HasAttr(prop, NetworkedAttr)) {
                AttributeData attr = prop.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == NetworkedAttr);
                NetField? f = MapField(prop, attr);
                if (f is not null) fields.Add(f.Value);
            }
            else if (isNetworkBehaviour && member is IMethodSymbol method && HasAttr(method, RpcAttr)) {
                NetRpc? rpc = MapRpc(method);
                if (rpc is not null) rpcs.Add(rpc.Value);
            }
        }

        if (isSceneReplicated) {
            if (fields.Count == 0)
                return null;
            return new NetTarget(
                symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                symbol.Name, symbol.ToDisplayString(), fields, rpcs, sceneReplicated: true);
        }

        if (fields.Count == 0 && rpcs.Count == 0) {
            bool isPartial = decl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
            if (!isPartial)
                return null;
        }

        return new NetTarget(
            symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            symbol.ToDisplayString(),
            fields,
            rpcs);
    }

    static bool DerivesFromNetworkBehaviour(INamedTypeSymbol symbol) {
        for (INamedTypeSymbol? t = symbol.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == NetworkBehaviourFullName)
                return true;
        return false;
    }

    static bool ImplementsIReplicated(INamedTypeSymbol symbol) {
        foreach (INamedTypeSymbol i in symbol.AllInterfaces)
            if (i.ToDisplayString() == IReplicatedFullName)
                return true;
        return false;
    }

    static bool HasAttr(ISymbol s, string fullName) =>
        s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    static NetField? MapField(IPropertySymbol prop, AttributeData attr) {
        string type = prop.Type.ToDisplayString();
        WireKind kind = type switch {
            "bool" => WireKind.Bool,
            "byte" => WireKind.Byte,
            "int" => WireKind.Int,
            "uint" => WireKind.UInt,
            "float" => WireKind.Float,
            "System.Numerics.Vector2" => WireKind.Vector2,
            "System.Numerics.Vector3" => WireKind.Vector3,
            "System.Numerics.Quaternion" => WireKind.Quaternion,
            _ => WireKind.Unsupported,
        };
        if (kind == WireKind.Unsupported)
            return null;

        float min = 0f, max = 0f;
        int bits = 0;
        foreach (KeyValuePair<string, TypedConstant> na in attr.NamedArguments) {
            switch (na.Key) {
                case "Min": min = ToFloat(na.Value); break;
                case "Max": max = ToFloat(na.Value); break;
                case "Bits": bits = na.Value.Value is int b ? b : 0; break;
            }
        }
        bool quantized = kind == WireKind.Float && bits > 0;
        return new NetField(prop.Name, kind, quantized, min, max, bits);
    }

    static float ToFloat(TypedConstant c) => c.Value switch {
        float f => f, double d => (float)d, int i => i, _ => 0f,
    };

    static NetRpc? MapRpc(IMethodSymbol method) {
        AttributeData attr = method.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == RpcAttr);

        int target = 0;
        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int t)
            target = t;

        bool reliable = true;
        foreach (KeyValuePair<string, TypedConstant> na in attr.NamedArguments)
            if (na.Key == "Reliable" && na.Value.Value is bool b)
                reliable = b;

        if (method.ReturnType.SpecialType != SpecialType.System_Void)
            return null;
        if (!method.IsPartialDefinition)
            return null;

        var args = new List<NetField>();
        foreach (IParameterSymbol p in method.Parameters) {
            NetField? f = MapArg(p);
            if (f is null)
                return null;
            args.Add(f.Value);
        }

        return new NetRpc(method.Name, target, reliable, args);
    }

    static NetField? MapArg(IParameterSymbol p) {
        WireKind kind = p.Type.ToDisplayString() switch {
            "bool" => WireKind.Bool,
            "byte" => WireKind.Byte,
            "int" => WireKind.Int,
            "uint" => WireKind.UInt,
            "float" => WireKind.Float,
            "System.Numerics.Vector2" => WireKind.Vector2,
            "System.Numerics.Vector3" => WireKind.Vector3,
            "System.Numerics.Quaternion" => WireKind.Quaternion,
            _ => WireKind.Unsupported,
        };
        if (kind == WireKind.Unsupported)
            return null;
        return new NetField(p.Name, kind, quantized: false, 0f, 0f, 0);
    }

    static void Emit(SourceProductionContext spc, NetTarget t) {
        if (t.Fields.Count > MaxNetworkedFields) {
            spc.ReportDiagnostic(Diagnostic.Create(TooManyNetworkedFields, Location.None, t.FullName, t.Fields.Count));
            return;
        }
        if (t.SceneReplicated) { EmitSceneReplicated(spc, t); return; }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> BallisticEngine.SourceGen — networking replication (plan §11).");
        sb.AppendLine("// Browsable + debuggable (the source-generator advantage over IL weaving).");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using BallisticEngine.Networking;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        bool hasNs = t.Namespace is not null;
        if (hasNs) { sb.AppendLine($"namespace {t.Namespace} {{"); }

        sb.AppendLine($"    partial class {t.TypeName} {{");

        int typeId = Fnv(new[] { t.FullName });
        int layoutHash = Fnv(t.Fields.Select(LayoutToken).ToArray());

        sb.AppendLine($"        public override bool HasNetworkedState => {(t.Fields.Count > 0 ? "true" : "false")};");
        sb.AppendLine($"        public override int NetworkTypeId => {typeId};");
        sb.AppendLine($"        public override int NetworkLayoutHash => {layoutHash};");
        sb.AppendLine();

        EmitBaseline(sb, t);
        EmitSerialize(sb, t);
        EmitSerializeFull(sb, t);
        EmitDeserialize(sb, t);
        EmitCaptureBaseline(sb, t);
        EmitRpcs(sb, t);
        EmitRegistration(sb, t, typeId, layoutHash);

        sb.AppendLine("    }");
        if (hasNs) sb.AppendLine("}");

        spc.AddSource($"{t.FullName.Replace('.', '_')}.Net.g.cs", sb.ToString());
    }

    static void EmitSceneReplicated(SourceProductionContext spc, NetTarget t) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> BallisticEngine.SourceGen — entity-less GameState replication (plan §2/§10).");
        sb.AppendLine("// The IReplicated carve-out: same wire shape as NetworkBehaviour, addressed by ReplicationId.");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("using BallisticEngine.Networking;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        bool hasNs = t.Namespace is not null;
        if (hasNs) sb.AppendLine($"namespace {t.Namespace} {{");
        sb.AppendLine($"    partial class {t.TypeName} {{");

        int typeId = Fnv(new[] { t.FullName });
        int layoutHash = Fnv(t.Fields.Select(LayoutToken).ToArray());

        sb.AppendLine($"        public override bool HasReplicatedState => true;");
        sb.AppendLine($"        public override int ReplicationTypeId => {typeId};");
        sb.AppendLine($"        public override int ReplicationLayoutHash => {layoutHash};");
        sb.AppendLine();

        EmitBaseline(sb, t);

        sb.Append("        public override bool IsDirty => ");
        for (int i = 0; i < t.Fields.Count; i++) {
            if (i > 0) sb.Append(" || ");
            sb.Append($"!__NetEq(this.{t.Fields[i].Name}, __netBaseline.{t.Fields[i].Name})");
        }
        sb.AppendLine(";");
        sb.AppendLine();

        sb.AppendLine("        public override void Serialize(BitWriter __w) {");
        sb.AppendLine("            uint __mask = 0;");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if (!__NetEq(this.{t.Fields[i].Name}, __netBaseline.{t.Fields[i].Name})) __mask |= {1u << i}u;");
        sb.AppendLine($"            __w.WriteBits(__mask, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) {WriteCall(t.Fields[i], $"this.{t.Fields[i].Name}")};");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public override void SerializeFull(BitWriter __w) {");
        uint fullMask = t.Fields.Count == 32 ? 0xFFFFFFFFu : (1u << t.Fields.Count) - 1u;
        sb.AppendLine($"            __w.WriteBits({fullMask}u, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            {WriteCall(t.Fields[i], $"this.{t.Fields[i].Name}")};");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public override void Deserialize(ref BitReader __r) {");
        sb.AppendLine($"            uint __mask = __r.ReadBits({t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) this.{t.Fields[i].Name} = {ReadCall(t.Fields[i])};");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public override void CaptureReplBaseline() {");
        foreach (NetField f in t.Fields)
            sb.AppendLine($"            __netBaseline.{f.Name} = this.{f.Name};");
        sb.AppendLine("        }");
        sb.AppendLine("        public override void ClearDirty() => CaptureReplBaseline();");
        sb.AppendLine();

        sb.AppendLine("        public override object __GetReplBaseline() => __netBaseline;");
        sb.AppendLine("        public override void __SetReplBaseline(object __b) {");
        sb.AppendLine("            if (__b is __NetBaseline __v) __netBaseline = __v;");
        sb.AppendLine("        }");
        sb.AppendLine("        public override bool __ReplStateEquals(object __b) {");
        sb.AppendLine("            if (__b is not __NetBaseline __v) return false;");
        if (t.Fields.Count == 0) {
            sb.AppendLine("            return true;");
        } else {
            sb.Append("            return ");
            for (int i = 0; i < t.Fields.Count; i++) {
                if (i > 0) sb.Append(" && ");
                sb.Append($"__NetEq(this.{t.Fields[i].Name}, __v.{t.Fields[i].Name})");
            }
            sb.AppendLine(";");
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        private static bool __NetEq<TVal>(TVal a, TVal b) =>");
        sb.AppendLine("            System.Collections.Generic.EqualityComparer<TVal>.Default.Equals(a, b);");
        sb.AppendLine();

        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        internal static void __SceneReplRegister() {");
        sb.AppendLine("            BallisticEngine.SceneReplicationRegistry.Register(");
        sb.AppendLine($"                new BallisticEngine.SceneReplDescriptor({typeId}, {layoutHash}, \"{t.FullName}\"));");
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        if (hasNs) sb.AppendLine("}");
        spc.AddSource($"{t.FullName.Replace('.', '_')}.SceneRepl.g.cs", sb.ToString());
    }

    static void EmitBaseline(StringBuilder sb, NetTarget t) {
        sb.AppendLine("        // The last-ack delta baseline (§11) — SerializeState diffs the live values against this.");
        sb.AppendLine("        private struct __NetBaseline {");
        foreach (NetField f in t.Fields)
            sb.AppendLine($"            public {ClrType(f.Kind)} {f.Name};");
        sb.AppendLine("        }");
        sb.AppendLine("        private __NetBaseline __netBaseline;");
        sb.AppendLine();
    }

    static void EmitSerialize(StringBuilder sb, NetTarget t) {
        sb.AppendLine("        public override void SerializeState(BitWriter __w) {");
        if (t.Fields.Count == 0) { sb.AppendLine("        }"); sb.AppendLine(); return; }

        sb.AppendLine("            uint __mask = 0;");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if (!__NetEq(this.{t.Fields[i].Name}, __netBaseline.{t.Fields[i].Name})) __mask |= {1u << i}u;");
        sb.AppendLine($"            __w.WriteBits(__mask, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++) {
            NetField f = t.Fields[i];
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) {WriteCall(f, $"this.{f.Name}")};");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    static void EmitSerializeFull(StringBuilder sb, NetTarget t) {
        sb.AppendLine("        public override void SerializeFullState(BitWriter __w) {");
        if (t.Fields.Count == 0) { sb.AppendLine("        }"); sb.AppendLine(); return; }
        uint fullMask = t.Fields.Count == 32 ? 0xFFFFFFFFu : (1u << t.Fields.Count) - 1u;
        sb.AppendLine($"            __w.WriteBits({fullMask}u, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++) {
            NetField f = t.Fields[i];
            sb.AppendLine($"            {WriteCall(f, $"this.{f.Name}")};");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    static void EmitDeserialize(StringBuilder sb, NetTarget t) {
        sb.AppendLine("        public override void DeserializeState(ref BitReader __r) {");
        if (t.Fields.Count == 0) { sb.AppendLine("        }"); sb.AppendLine(); return; }
        sb.AppendLine($"            uint __mask = __r.ReadBits({t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++) {
            NetField f = t.Fields[i];
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) this.{f.Name} = {ReadCall(f)};");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    static void EmitCaptureBaseline(StringBuilder sb, NetTarget t) {
        sb.AppendLine("        public override void CaptureNetworkBaseline() {");
        foreach (NetField f in t.Fields)
            sb.AppendLine($"            __netBaseline.{f.Name} = this.{f.Name};");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public override object __GetNetBaseline() => __netBaseline;");
        sb.AppendLine("        public override void __SetNetBaseline(object __b) {");
        sb.AppendLine("            if (__b is __NetBaseline __v) __netBaseline = __v;");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public override bool __NetStateEquals(object __b) {");
        sb.AppendLine("            if (__b is not __NetBaseline __v) return false;");
        if (t.Fields.Count == 0) {
            sb.AppendLine("            return true;");
        } else {
            sb.Append("            return ");
            for (int i = 0; i < t.Fields.Count; i++) {
                if (i > 0) sb.Append(" && ");
                sb.Append($"__NetEq(this.{t.Fields[i].Name}, __v.{t.Fields[i].Name})");
            }
            sb.AppendLine(";");
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        private static bool __NetEq<TVal>(TVal a, TVal b) =>");
        sb.AppendLine("            System.Collections.Generic.EqualityComparer<TVal>.Default.Equals(a, b);");
        sb.AppendLine();
    }

    static void EmitRpcs(StringBuilder sb, NetTarget t) {
        if (t.Rpcs.Count == 0)
            return;
        string[] targetEnum = { "Server", "Owner", "All" };
        foreach (NetRpc rpc in t.Rpcs) {
            int methodId = FnvString(rpc.Name);
            string sig = string.Join(", ", rpc.Args.Select(a => $"{ArgClrType(a.Kind)} {a.Name}"));
            string callArgs = string.Join(", ", rpc.Args.Select(a => a.Name));

            sb.AppendLine($"        // [Rpc(To.{targetEnum[rpc.Target]}{(rpc.Reliable ? "" : ", Reliable=false")})] generated send stub.");
            sb.AppendLine($"        public partial void {rpc.Name}({sig}) {{");
            sb.AppendLine("            var __aw = new BitWriter();");
            foreach (NetField a in rpc.Args)
                sb.AppendLine($"            {WriteCall(a, a.Name, "__aw")};");
            sb.AppendLine($"            BallisticEngine.Network.SendRpc(this, {methodId}, " +
                          $"BallisticEngine.Networking.RpcTarget.{targetEnum[rpc.Target]}, {(rpc.Reliable ? "true" : "false")}, __aw.AsSpan());");
            sb.AppendLine("        }");

            sb.AppendLine($"        private static void __Invoke_{rpc.Name}(BallisticEngine.NetworkBehaviour __self, ref BitReader __r, Connection __caller) {{");
            for (int i = 0; i < rpc.Args.Count; i++) {
                NetField a = rpc.Args[i];
                sb.AppendLine($"            {ArgClrType(a.Kind)} {a.Name} = {ReadCall(a)};");
            }
            sb.AppendLine($"            (({t.TypeName})__self).{rpc.Name}Impl({callArgs});");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
    }

    static string ArgClrType(WireKind k) => ClrType(k);

    static void EmitRegistration(StringBuilder sb, NetTarget t, int typeId, int layoutHash) {
        string[] targetEnum = { "Server", "Owner", "All" };
        string rpcArray;
        if (t.Rpcs.Count == 0) {
            rpcArray = "System.Array.Empty<BallisticEngine.NetworkRpcEntry>()";
        } else {
            var entries = t.Rpcs.Select(r =>
                $"new BallisticEngine.NetworkRpcEntry({FnvString(r.Name)}, " +
                $"BallisticEngine.Networking.RpcTarget.{targetEnum[r.Target]}, {(r.Reliable ? "true" : "false")}, " +
                $"__Invoke_{r.Name})");
            rpcArray = "new BallisticEngine.NetworkRpcEntry[] { " + string.Join(", ", entries) + " }";
        }
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine($"        internal static void __NetRegister() {{");
        sb.AppendLine($"            BallisticEngine.NetworkReplicationRegistry.Register(");
        sb.AppendLine($"                new BallisticEngine.NetworkTypeDescriptor({typeId}, {layoutHash}, \"{t.FullName}\", {rpcArray},");
        sb.AppendLine($"                    typeof({t.FullName})));");
        sb.AppendLine("        }");
    }

    static string WriteCall(NetField f, string expr, string writer = "__w") => f.Quantized
        ? $"WireCodec.WriteQ({writer}, {expr}, {Lit(f.Min)}, {Lit(f.Max)}, {f.Bits})"
        : $"WireCodec.Write({writer}, {expr})";

    static string ReadCall(NetField f) => f.Kind switch {
        WireKind.Bool => "WireCodec.ReadBool(ref __r)",
        WireKind.Byte => "WireCodec.ReadByte(ref __r)",
        WireKind.Int => "WireCodec.ReadInt(ref __r)",
        WireKind.UInt => "WireCodec.ReadUInt(ref __r)",
        WireKind.Float => f.Quantized
            ? $"WireCodec.ReadQ(ref __r, {Lit(f.Min)}, {Lit(f.Max)}, {f.Bits})"
            : "WireCodec.ReadFloat(ref __r)",
        WireKind.Vector2 => "WireCodec.ReadVector2(ref __r)",
        WireKind.Vector3 => "WireCodec.ReadVector3(ref __r)",
        WireKind.Quaternion => "WireCodec.ReadQuaternion(ref __r)",
        _ => "default",
    };

    static string ClrType(WireKind k) => k switch {
        WireKind.Bool => "bool", WireKind.Byte => "byte", WireKind.Int => "int", WireKind.UInt => "uint",
        WireKind.Float => "float", WireKind.Vector2 => "System.Numerics.Vector2",
        WireKind.Vector3 => "System.Numerics.Vector3", WireKind.Quaternion => "System.Numerics.Quaternion",
        _ => "object",
    };

    static string LayoutToken(NetField f) {
        string kindTag = f.Kind switch {
            WireKind.Bool => "bool", WireKind.Byte => "u8", WireKind.Int => "i32", WireKind.UInt => "u32",
            WireKind.Float => f.Quantized ? $"q[{f.Min},{f.Max},{f.Bits}]" : "f32",
            WireKind.Vector2 => "f32x2", WireKind.Vector3 => "f32x3", WireKind.Quaternion => "f32x4",
            _ => "?",
        };
        return $"{f.Name}:{kindTag}";
    }

    static string Lit(float f) {
        string s = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!s.Contains(".") && !s.Contains("E") && !s.Contains("e")) s += ".0";
        return s + "f";
    }

    static int Fnv(string[] tokens) {
        unchecked {
            uint h = 2166136261;
            foreach (string t in tokens) {
                foreach (char c in t) { h ^= c; h *= 16777619; }
                h ^= (byte)'|'; h *= 16777619;
            }
            return (int)h;
        }
    }

    static int FnvString(string s) {
        unchecked {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    enum WireKind { Unsupported, Bool, Byte, Int, UInt, Float, Vector2, Vector3, Quaternion }

    readonly struct NetField {
        public readonly string Name;
        public readonly WireKind Kind;
        public readonly bool Quantized;
        public readonly float Min, Max;
        public readonly int Bits;
        public NetField(string name, WireKind kind, bool quantized, float min, float max, int bits) {
            Name = name; Kind = kind; Quantized = quantized; Min = min; Max = max; Bits = bits;
        }
    }

    readonly struct NetRpc {
        public readonly string Name;
        public readonly int Target;
        public readonly bool Reliable;
        public readonly IReadOnlyList<NetField> Args;

        public NetRpc(string name, int target, bool reliable, IReadOnlyList<NetField> args) {
            Name = name; Target = target; Reliable = reliable; Args = args;
        }
    }

    sealed class NetTarget {
        public readonly string? Namespace;
        public readonly string TypeName;
        public readonly string FullName;
        public readonly IReadOnlyList<NetField> Fields;
        public readonly IReadOnlyList<NetRpc> Rpcs;
        public readonly bool SceneReplicated;

        public NetTarget(string? ns, string typeName, string fullName,
            IReadOnlyList<NetField> fields, IReadOnlyList<NetRpc> rpcs, bool sceneReplicated = false) {
            Namespace = ns; TypeName = typeName; FullName = fullName; Fields = fields; Rpcs = rpcs;
            SceneReplicated = sceneReplicated;
        }
    }
}
