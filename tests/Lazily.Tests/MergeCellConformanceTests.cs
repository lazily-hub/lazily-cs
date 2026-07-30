using Xunit;

namespace Lazily.Tests;

public sealed class MergeCellConformanceTests
{
    [Fact]
    public void ReplaysTheCanonicalMergeCellAlgebra()
    {
        using var document = SpecCorpus.Load("collections", "mergecell_algebra.json");
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(3, scenarios.Length);
        var assertions = 0;

        foreach (var scenario in scenarios)
        {
            var policyName = scenario.GetProperty("policy").GetString()!;
            var policy = Policy(policyName);
            var flags = scenario.GetProperty("flags");
            Assert.Equal(flags.GetProperty("commutative").GetBoolean(), policy.Commutative);
            Assert.Equal(flags.GetProperty("idempotent").GetBoolean(), policy.Idempotent);
            assertions += 2;

            var context = new Context();
            var source = context.Source(scenario.GetProperty("initial").GetInt64(), policy);
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                var before = source.Get();
                source.Merge(step.GetProperty("merge").GetInt64());
                var after = source.Get();
                var expected = FixtureAssertions.Of(
                    step,
                    "expected",
                    $"collections/mergecell_algebra.json scenario {policyName}");
                Assert.Equal(expected.GetProperty("value").GetInt64(), after);
                Assert.Equal(expected.GetProperty("invalidates").GetBoolean(), before != after);
                expected.Verify();
                assertions += 2;
            }
        }

        Assert.True(assertions >= 30, "fixture replay asserted too little");
    }

    private static MergePolicy<long> Policy(string name) =>
        name switch
        {
            "KeepLatest" => MergePolicy.KeepLatest<long>(),
            "Sum" => MergePolicy.Sum<long>(),
            "Max" => MergePolicy.Max<long>(),
            _ => throw new InvalidOperationException($"Unknown merge policy {name}."),
        };
}
