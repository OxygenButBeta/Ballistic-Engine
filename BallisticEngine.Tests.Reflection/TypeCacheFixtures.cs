namespace BallisticEngine.Tests.Reflection;

// Sample types exercising every branch of TypeCache's concrete+closed-generic+instantiable filter.
// Each name states what it proves. Kept in one file so the expected-set assertions read against a
// known, closed universe.

// ── Derived-type queries (GetTypesDerivedFrom<T>) ──────────────────────────────────────────────

// The polymorphic base: an interface (NOT itself instantiable, must be excluded from its own query).
public interface ISample { }

// A concrete instantiable implementor with a public parameterless ctor → MUST be returned.
public sealed class SampleA : ISample { }

// A second concrete implementor → MUST be returned.
public sealed class SampleB : ISample { }

// Abstract implementor → NOT instantiable, MUST be excluded.
public abstract class SampleAbstract : ISample { }

// Concrete subclass of the abstract one → instantiable, MUST be returned (proves transitive assignability).
public sealed class SampleConcreteSub : SampleAbstract { }

// Implementor with NO public parameterless ctor → can't be `new`'d for a dropdown, MUST be excluded.
public sealed class SampleNoDefaultCtor : ISample {
    public SampleNoDefaultCtor(int required) { _ = required; }
}

// Implementor whose only ctor is non-public → MUST be excluded (GetConstructor(EmptyTypes) is public-only).
public sealed class SamplePrivateCtor : ISample {
    private SamplePrivateCtor() { }
}

// Open generic implementor (T unbound) → an unsupported [SerializeReference] field type, MUST be excluded.
// (Non-sealed only so SampleClosedGeneric below can derive a closed construction from it.)
public class SampleOpenGeneric<T> : ISample { }

// A CLOSED construction of the open generic → instantiable, MUST be returned (closed-generic parity).
public sealed class SampleClosedGeneric : SampleOpenGeneric<int> { }

// An abstract BASE used as the query type directly (not an interface) — proves an abstract base is
// excluded from its own derived-from query while its concrete subclass is included.
public abstract class SampleAbstractBase { }
public sealed class SampleAbstractBaseSub : SampleAbstractBase { }

// A CONCRETE base used as the query type — proves a concrete instantiable base IS returned for its own
// query (alongside its subclass).
public class SampleConcreteBase { }
public sealed class SampleConcreteBaseSub : SampleConcreteBase { }

// ── Attribute queries (GetTypesWithAttribute / GetMethodsWithAttribute) ─────────────────────────

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SampleMarkerAttribute : Attribute { }

// Concrete type carrying the marker → MUST be returned by GetTypesWithAttribute.
[SampleMarker]
public sealed class SampleMarked { }

// Abstract type carrying the marker → NOT instantiable, MUST be excluded (attribute query is concrete-only).
[SampleMarker]
public abstract class SampleMarkedAbstract { }

// Host for static methods carrying the marker (the [MenuItem] window-discovery shape). NOT a static
// class — it also hosts a marked INSTANCE method that the static-only query must skip.
public class SampleMenuHost {
    [SampleMarker]
    public static void MarkedStatic() { }

    // Public static but UNmarked → MUST NOT appear.
    public static void UnmarkedStatic() { }

    // Marked but INSTANCE (not static) → MUST NOT appear (window methods are static).
    [SampleMarker]
    public void MarkedInstance() { }
}
