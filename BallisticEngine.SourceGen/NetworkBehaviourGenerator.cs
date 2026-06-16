using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BallisticEngine.SourceGen;

// THE generator (plan §11). For every NetworkBehaviour subtype that declares [Networked] properties
// and/or [Rpc] methods, emits a `partial` with:
//   - a private Baseline struct (value copy of the [Networked] fields) — the delta diff target
//   - SerializeState(BitWriter): a FieldCount-bit changemask + only-changed fields (delta, §11)
//   - DeserializeState(ref BitReader): read mask, apply only changed fields
//   - CaptureNetworkBaseline(): snapshot current values as the next baseline
//   - NetworkTypeId / NetworkLayoutHash / HasNetworkedState overrides
//   - a [ModuleInitializer] that registers the type into NetworkReplicationRegistry (load-once, gate 0c)
//
// The wire-kind mapping + the WireCodec calls are BYTE-IDENTICAL to the harness-proven format
// (%TEMP%\bal-netserde-test). The generated C# is browsable (the §11 advantage over IL weaving).
[Generator(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class NetworkBehaviourGenerator : IIncrementalGenerator {
    const string NetworkBehaviourFullName = "BallisticEngine.NetworkBehaviour";
    const string NetworkedAttr = "BallisticEngine.NetworkedAttribute";
    const string RpcAttr = "BallisticEngine.RpcAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax c && c.BaseList is not null,
            transform: static (ctx, _) => GetTarget(ctx))
            .Where(static t => t is not null);

        context.RegisterSourceOutput(candidates, static (spc, target) => Emit(spc, target!));
    }

    // ---- discovery: is this class a NetworkBehaviour subtype with [Networked]/[Rpc] members? ---------
    static NetTarget? GetTarget(GeneratorSyntaxContext ctx) {
        var decl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol)
            return null;
        if (!DerivesFromNetworkBehaviour(symbol))
            return null;

        var fields = new List<NetField>();
        var rpcs = new List<NetRpc>();

        foreach (ISymbol member in symbol.GetMembers()) {
            if (member is IPropertySymbol prop && HasAttr(prop, NetworkedAttr)) {
                AttributeData attr = prop.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == NetworkedAttr);
                NetField? f = MapField(prop, attr);
                if (f is not null) fields.Add(f.Value);
            }
            else if (member is IMethodSymbol method && HasAttr(method, RpcAttr)) {
                NetRpc? rpc = MapRpc(method);
                if (rpc is not null) rpcs.Add(rpc.Value);
            }
        }

        if (fields.Count == 0 && rpcs.Count == 0)
            return null;   // a NetworkBehaviour with no replicated state/RPCs — generate nothing (§11 scoping)

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

    static bool HasAttr(ISymbol s, string fullName) =>
        s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    // Map a [Networked] property to a wire field, honoring opt-in quantization. Returns null for an
    // unsupported type (emits no field; a diagnostic is a follow-up — P2 supports the documented set).
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

        // Opt-in quantization (Bits/Min/Max named args) — only valid for float (§11). A quantized non-float
        // is ignored (falls back to full precision) — P2 supports float quantization, the documented case.
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

    // Map an [Rpc(To.X, Reliable=...)] method to a wire RPC (plan §4b, P4). Captures the target (the ctor
    // arg), reliability (named arg, default true), and the typed parameter list (same WireKind map as a
    // [Networked] field). Returns null + emits a diagnostic for a method that breaks the contract:
    //   - must be `partial` (the generator supplies the body — the chosen Fusion-like ergonomic)
    //   - must return void (L1 — there is NO RPC return; request→response is RPC-up + state-down)
    //   - every parameter must be a supported wire type
    static NetRpc? MapRpc(IMethodSymbol method) {
        AttributeData attr = method.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == RpcAttr);

        // To.X — the single ctor argument (RpcTarget enum: 0=Server, 1=Owner, 2=All).
        int target = 0;
        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int t)
            target = t;

        // Reliable — named arg, default true (reliable by default; Rpc.Unreliable opt-in for spammy FX).
        bool reliable = true;
        foreach (KeyValuePair<string, TypedConstant> na in attr.NamedArguments)
            if (na.Key == "Reliable" && na.Value.Value is bool b)
                reliable = b;

        // The method must be partial + void (the generator emits the body; L1 = no return). A non-partial
        // or non-void [Rpc] is a contract break — skip it (the C# compiler also errors on a partial decl
        // with no impl, so a partial-void with no body is the only valid form, which is exactly what we fill).
        if (method.ReturnType.SpecialType != SpecialType.System_Void)
            return null;   // L1: an RPC returns nothing
        if (!method.IsPartialDefinition)
            return null;   // the body is generated; the dev declares it partial (chosen ergonomic)

        var args = new List<NetField>();
        foreach (IParameterSymbol p in method.Parameters) {
            NetField? f = MapArg(p);
            if (f is null)
                return null;   // an unsupported arg type — skip the whole method (a diagnostic is a follow-up)
            args.Add(f.Value);
        }

        return new NetRpc(method.Name, target, reliable, args);
    }

    // Map one RPC parameter to a wire field (reusing the field kind map — same supported set as [Networked]:
    // bool/byte/int/uint/float/Vector2/Vector3/Quaternion). RPC args are full-precision (no per-arg quantize
    // token in P4 — quantization is a [Networked]-field concern; a quantized RPC arg is a follow-up).
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

    // ---- emission ----------------------------------------------------------------------------------
    static void Emit(SourceProductionContext spc, NetTarget t) {
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

        // typeId + layout hash (computed at codegen time with the SAME FNV the engine ships).
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
        EmitRpcs(sb, t);                 // P4: partial-void send stubs + reflection-free invokers
        EmitRegistration(sb, t, typeId, layoutHash);

        sb.AppendLine("    }");
        if (hasNs) sb.AppendLine("}");

        spc.AddSource($"{t.FullName.Replace('.', '_')}.Net.g.cs", sb.ToString());
    }

    static void EmitBaseline(StringBuilder sb, NetTarget t) {
        // The delta baseline — a value copy of the [Networked] fields, captured after each ack. Stored on
        // the component (the generated __netBaseline field). A struct so the diff is alloc-free.
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

        // Build the changemask (one comparison per field, in declaration order).
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
        // A FULL snapshot: the changemask is all-set, every field written. DeserializeState reads it back
        // identically (it just sees every mask bit set). This is the spawn / late-join baseline path.
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
        // A small typed equality helper so the changemask comparison is a value compare (no boxing).
        sb.AppendLine("        private static bool __NetEq<TVal>(TVal a, TVal b) =>");
        sb.AppendLine("            System.Collections.Generic.EqualityComparer<TVal>.Default.Equals(a, b);");
        sb.AppendLine();
    }

    // P4: for each [Rpc] method emit (a) the partial-void STUB body — pack args + route via Network.SendRpc;
    // (b) a reflection-free INVOKER — deserialize args + call the dev's <Name>Impl. The dev declares the
    // method `partial` (no body) and writes `<Name>Impl(<same args>)` with the logic; `RpcCaller` exposes
    // who fired it. This is the Fusion-like ergonomic the owner chose — the call site is just `weapon.Fire(dir)`.
    static void EmitRpcs(StringBuilder sb, NetTarget t) {
        if (t.Rpcs.Count == 0)
            return;
        string[] targetEnum = { "Server", "Owner", "All" };
        foreach (NetRpc rpc in t.Rpcs) {
            int methodId = FnvString(rpc.Name);
            string sig = string.Join(", ", rpc.Args.Select(a => $"{ArgClrType(a.Kind)} {a.Name}"));
            string callArgs = string.Join(", ", rpc.Args.Select(a => a.Name));

            // (a) the partial method body — pack args into a fresh BitWriter, hand the bytes to the router.
            sb.AppendLine($"        // [Rpc(To.{targetEnum[rpc.Target]}{(rpc.Reliable ? "" : ", Reliable=false")})] generated send stub.");
            sb.AppendLine($"        public partial void {rpc.Name}({sig}) {{");
            sb.AppendLine("            var __aw = new BitWriter();");
            foreach (NetField a in rpc.Args)
                sb.AppendLine($"            {WriteCall(a, a.Name, "__aw")};");
            sb.AppendLine($"            BallisticEngine.Network.SendRpc(this, {methodId}, " +
                          $"BallisticEngine.Networking.RpcTarget.{targetEnum[rpc.Target]}, {(rpc.Reliable ? "true" : "false")}, __aw.AsSpan());");
            sb.AppendLine("        }");

            // (b) the invoker — static so it's a cheap method-group delegate; reads args then calls <Name>Impl.
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
        // A [ModuleInitializer] registers this type's wire metadata into NetworkReplicationRegistry once
        // at module load (the only sanctioned reflection-free registration, §11). The registry is cleared
        // at the hot-reload boundary (gate 0c) so the next ALC re-registers.
        //
        // The RPC table (P4) = one NetworkRpcEntry per [Rpc] method: (methodId, target, reliable, invoker).
        // The invoker is a static method-group delegate the dispatch path calls with NO reflection (§11).
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
        // The concrete type builds the client-side mirror (P3 spawn replication) via Entity.AddComponent
        // (Type). The generator knows the type at codegen, so the spawn path resolves it without a reflection
        // SCAN (one typeof). The Type handle is a script-ALC root dropped by ClearForReload like the rest.
        sb.AppendLine($"                    typeof({t.FullName})));");
        sb.AppendLine("        }");
    }

    // ---- per-field codec calls (BYTE-IDENTICAL to the harness-proven WireCodec) --------------------
    // `writer` names the BitWriter local (the state serializers use __w; the RPC arg packer uses __aw).
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

    // The layout token — name + a stable wire-kind tag (+ quantize params) — feeds the layout hash. MUST
    // match the harness format so a hash here equals the one the engine computes at runtime over the same
    // field. Reordering / retyping / re-quantizing a field shifts the hash (gate 0c drift detection).
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

    // FNV-1a 32-bit — IDENTICAL to BallisticEngine.Networking.WireCodec.Fnv (so codegen-time == runtime).
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

    // ---- model -------------------------------------------------------------------------------------
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
        public readonly int Target;          // RpcTarget: 0=Server, 1=Owner, 2=All
        public readonly bool Reliable;
        public readonly IReadOnlyList<NetField> Args;   // typed parameter list (wire-packed)
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
        public NetTarget(string? ns, string typeName, string fullName,
            IReadOnlyList<NetField> fields, IReadOnlyList<NetRpc> rpcs) {
            Namespace = ns; TypeName = typeName; FullName = fullName; Fields = fields; Rpcs = rpcs;
        }
    }
}
