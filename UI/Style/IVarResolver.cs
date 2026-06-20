namespace BallisticEngine.UI;

// Resolves CSS custom properties (var(--name)). The document collects `--token: value` declarations
// (from :root and any rule) into a store implementing this; StyleApplier asks it to expand var() at
// apply time. Kept as an interface so the resolver/document owns the store, not the applier. (P2.4)
public interface IVarResolver
{
    // Returns the value for a custom property name ("--accent"), or null/empty if undefined.
    string ResolveVar(string name);
}
