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
    const string IReplicatedFullName = "BallisticEngine.Networking.IReplicated";
    const string NetworkedAttr = "BallisticEngine.NetworkedAttribute";
    const string RpcAttr = "BallisticEngine.RpcAttribute";

    // The replication changemask is a single `uint` (one bit per [Networked] field) written via
    // `BitWriter.WriteBits(__mask, FieldCount)`, which caps at 32 bits. Past 32 fields the `1u << i`
    // mask bits wrap (C# masks the shift count to 5 bits → field 32 collides with field 0) AND
    // WriteBits(_, >32) throws at runtime — a SILENT-then-crashing failure. Fail the BUILD loudly
    // instead, so the dev splits the component (or this becomes a `ulong`/multi-chunk mask later).
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

    // ---- discovery: a NetworkBehaviour subtype with [Networked]/[Rpc], OR (P7) an IReplicated
    //      SceneBehaviour (GameState) with [Networked] members (the entity-less carve-out) ------------
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
                // RPCs are a NetworkBehaviour concept only — an IReplicated SceneBehaviour (GameState) has
                // no NetworkObject identity to address an RPC to (§2: GameMode/GameState are not the RPC path).
                NetRpc? rpc = MapRpc(method);
                if (rpc is not null) rpcs.Add(rpc.Value);
            }
        }

        if (isSceneReplicated) {
            // P7: an IReplicated SceneBehaviour (GameState) with NO [Networked] members ships nothing — the
            // base IReplicated no-ops in GameState handle it. Only generate when it carries state (§11 scoping).
            if (fields.Count == 0)
                return null;
            return new NetTarget(
                symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                symbol.Name, symbol.ToDisplayString(), fields, rpcs, sceneReplicated: true);
        }

        if (fields.Count == 0 && rpcs.Count == 0) {
            // A NetworkBehaviour with no [Networked]/[Rpc] members. P6: if it is declared `partial` it still
            // wants a typeId + registration so it can be mirror-SPAWNED (possession-replication: a bare
            // PlayerController/Pawn must build a client mirror via typeId->factory). We emit ONLY the
            // lightweight registration (no baseline/serialize machinery — there is no state). A NON-partial
            // bare NetworkBehaviour generates nothing (§11 scoping preserved — it can't be a partial target
            // and is not meant to replicate as its own identity, e.g. a pure RPC-relay helper).
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

    // P7: does this type implement IReplicated (the entity-less GameState carve-out)? AllInterfaces walks the
    // full interface set (so a subclass of a GameState that already implements it is also caught).
    static bool ImplementsIReplicated(INamedTypeSymbol symbol) {
        foreach (INamedTypeSymbol i in symbol.AllInterfaces)
            if (i.ToDisplayString() == IReplicatedFullName)
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
        if (t.Fields.Count > MaxNetworkedFields) {
            // The uint changemask can't address more than 32 fields (see MaxNetworkedFields). Report a build
            // error and emit nothing for this type — a wrapped mask would silently desync, then crash.
            spc.ReportDiagnostic(Diagnostic.Create(TooManyNetworkedFields, Location.None, t.FullName, t.Fields.Count));
            return;
        }
        if (t.SceneReplicated) { EmitSceneReplicated(spc, t); return; }   // P7: the entity-less GameState path

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

    // ---- P7: the ENTITY-LESS IReplicated SceneBehaviour (GameState) emit (plan §2/§10) ---------------
    // Same bit-packed wire shape as a NetworkBehaviour, but overrides the IReplicated surface (Serialize/
    // Deserialize/SerializeFull/CaptureReplBaseline/IsDirty) + the per-client baseline-swap trio
    // (__GetReplBaseline/__SetReplBaseline/__ReplStateEquals) + ReplicationTypeId/LayoutHash/
    // HasReplicatedState, and registers into SceneReplicationRegistry (NOT NetworkReplicationRegistry —
    // GameState is not spawned, so it has no client-mirror factory). The byte-level field codec is IDENTICAL
    // to the NetworkBehaviour path, so the layout-digest drift guard and the per-client baseline carry over.
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

        // The delta baseline struct + storage (identical to EmitBaseline, reused).
        EmitBaseline(sb, t);

        // IsDirty — any field differs from the baseline (the §11 dirty bit the tick uses to skip).
        sb.Append("        public override bool IsDirty => ");
        for (int i = 0; i < t.Fields.Count; i++) {
            if (i > 0) sb.Append(" || ");
            sb.Append($"!__NetEq(this.{t.Fields[i].Name}, __netBaseline.{t.Fields[i].Name})");
        }
        sb.AppendLine(";");
        sb.AppendLine();

        // Serialize (delta) — changemask + only-changed fields vs the baseline.
        sb.AppendLine("        public override void Serialize(BitWriter __w) {");
        sb.AppendLine("            uint __mask = 0;");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if (!__NetEq(this.{t.Fields[i].Name}, __netBaseline.{t.Fields[i].Name})) __mask |= {1u << i}u;");
        sb.AppendLine($"            __w.WriteBits(__mask, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) {WriteCall(t.Fields[i], $"this.{t.Fields[i].Name}")};");
        sb.AppendLine("        }");
        sb.AppendLine();

        // SerializeFull — every field (the spawn/late-join baseline).
        sb.AppendLine("        public override void SerializeFull(BitWriter __w) {");
        uint fullMask = t.Fields.Count == 32 ? 0xFFFFFFFFu : (1u << t.Fields.Count) - 1u;
        sb.AppendLine($"            __w.WriteBits({fullMask}u, {t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            {WriteCall(t.Fields[i], $"this.{t.Fields[i].Name}")};");
        sb.AppendLine("        }");
        sb.AppendLine();

        // Deserialize — read mask + changed fields.
        sb.AppendLine("        public override void Deserialize(ref BitReader __r) {");
        sb.AppendLine($"            uint __mask = __r.ReadBits({t.Fields.Count});");
        for (int i = 0; i < t.Fields.Count; i++)
            sb.AppendLine($"            if ((__mask & {1u << i}u) != 0) this.{t.Fields[i].Name} = {ReadCall(t.Fields[i])};");
        sb.AppendLine("        }");
        sb.AppendLine();

        // CaptureReplBaseline + ClearDirty (ClearDirty == capture: dirty is "live != baseline").
        sb.AppendLine("        public override void CaptureReplBaseline() {");
        foreach (NetField f in t.Fields)
            sb.AppendLine($"            __netBaseline.{f.Name} = this.{f.Name};");
        sb.AppendLine("        }");
        sb.AppendLine("        public override void ClearDirty() => CaptureReplBaseline();");
        sb.AppendLine();

        // The per-client baseline-swap trio (identical mechanism to NetworkBehaviour's, ReplBaseline names).
        sb.AppendLine("        public override object __GetReplBaseline() => __netBaseline;");
        sb.AppendLine("        public override void __SetReplBaseline(object __b) {");
        sb.AppendLine("            if (__b is __NetBaseline __v) __netBaseline = __v;");
        sb.AppendLine("        }");
        sb.AppendLine("        public override bool __ReplStateEquals(object __b) {");
        sb.AppendLine("            if (__b is not __NetBaseline __v) return false;");
        // Guard the 0-field case so it emits `return true;` not `return ;` (a non-void return needs a value) —
        // matches the NetworkBehaviour path. A 0-field SceneReplicated is filtered out before emit today, so
        // this is defensive: it keeps the emitted code valid if that gate ever changes.
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

        // Registration into SceneReplicationRegistry (the entity-less drift-guard root, gate 0c).
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

        // P6 PER-CLIENT BASELINE swap (plan §13 late-join): the component carries ONE delta baseline
        // (__netBaseline), but per-client replication needs SerializeState to diff against a DIFFERENT
        // baseline per client. So the manager SWAPS the active baseline around each per-client serialize:
        // Set(C's baseline) -> SerializeState -> the bytes are C's delta -> save the post-send baseline as
        // C's pending (Get). Get/Set box the baseline struct — on the 20Hz SEND path (per client, per
        // object), NOT the per-tick hot path, so the box is acceptable (the standing rule is per-FRAME/
        // per-DRAW reflection-free; this is neither, and there is no reflection). A no-[Networked] type
        // returns/accepts null (the base no-ops).
        sb.AppendLine("        public override object __GetNetBaseline() => __netBaseline;");
        sb.AppendLine("        public override void __SetNetBaseline(object __b) {");
        sb.AppendLine("            if (__b is __NetBaseline __v) __netBaseline = __v;");
        sb.AppendLine("        }");
        sb.AppendLine();

        // P6: live == the given baseline token? Lets the per-client flush skip a quiescent object (0 bytes)
        // without a probe/rewind. A null/foreign token compares not-equal (safe — sends the delta).
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
        public readonly bool SceneReplicated;   // P7: an IReplicated SceneBehaviour (GameState), entity-less path
        public NetTarget(string? ns, string typeName, string fullName,
            IReadOnlyList<NetField> fields, IReadOnlyList<NetRpc> rpcs, bool sceneReplicated = false) {
            Namespace = ns; TypeName = typeName; FullName = fullName; Fields = fields; Rpcs = rpcs;
            SceneReplicated = sceneReplicated;
        }
    }
}
