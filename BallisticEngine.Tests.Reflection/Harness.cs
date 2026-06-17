namespace BallisticEngine.Tests.Reflection;

// The Phase-0 headless test rig. A dependency-free check accumulator the way the physics/inspector
// scratch suites work (assert → count → honest exit code), but COMMITTED so every P0 chunk extends it
// instead of re-creating a %TEMP% throwaway. P0.2 adds resolve-plan-for-type checks, P0.4 adds drawer
// determinism checks, all against this same Check/CheckSet API.
public sealed class Harness {
    int passed;
    readonly List<string> failures = new();

    // Assert a boolean. `detail` is printed only on failure so a passing run stays quiet.
    public void Check(string name, bool condition, string detail = null) {
        if (condition) {
            passed++;
        } else {
            failures.Add(detail is null ? name : $"{name} — {detail}");
        }
    }

    // Assert two type SETS are equal regardless of order (TypeCache returns ordered lists; order is
    // checked separately where it matters). Prints the symmetric difference on failure.
    public void CheckSet(string name, IEnumerable<Type> actual, params Type[] expected) {
        var a = new HashSet<Type>(actual);
        var e = new HashSet<Type>(expected);
        if (a.SetEquals(e)) {
            passed++;
            return;
        }
        var missing = e.Except(a).Select(t => t.Name);
        var extra = a.Except(e).Select(t => t.Name);
        failures.Add($"{name} — missing: [{string.Join(", ", missing)}]  unexpected: [{string.Join(", ", extra)}]");
    }

    // Assert a sequence equals the expected order exactly (for the determinism contract: ordered output).
    public void CheckSequence(string name, IEnumerable<Type> actual, params Type[] expected) {
        var a = actual.ToArray();
        if (a.SequenceEqual(expected)) {
            passed++;
            return;
        }
        failures.Add($"{name} — got [{string.Join(", ", a.Select(t => t.Name))}] expected [{string.Join(", ", expected.Select(t => t.Name))}]");
    }

    // Assert a string sequence equals the expected order exactly (the property model's ordering checks
    // produce string keys, not Types — CheckSequence is Type-only).
    public void CheckStrings(string name, IEnumerable<string> actual, params string[] expected) {
        var a = actual.ToArray();
        if (a.SequenceEqual(expected)) {
            passed++;
            return;
        }
        failures.Add($"{name} — got [{string.Join(", ", a)}] expected [{string.Join(", ", expected)}]");
    }

    // Print the summary and return the process exit code (0 = all passed, else the failure count).
    public int Report(string suite) {
        int total = passed + failures.Count;
        if (failures.Count == 0) {
            Console.WriteLine($"[{suite}] OK — {passed}/{total} checks passed.");
            return 0;
        }
        Console.WriteLine($"[{suite}] FAIL — {passed}/{total} passed, {failures.Count} failed:");
        foreach (string f in failures)
            Console.WriteLine($"    ✗ {f}");
        return failures.Count;
    }
}
