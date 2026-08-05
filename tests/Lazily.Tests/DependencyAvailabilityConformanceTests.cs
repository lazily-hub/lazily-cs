using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

public sealed class DependencyAvailabilityConformanceTests
{
    [Fact]
    public void ExactKeyDependencyAvailabilityFixture()
    {
        const string fixture = "dependency_reactive_availability.json";
        using var document = SpecCorpus.Load("collections", fixture);
        var root = document.RootElement;
        var wanted = root.GetProperty("key").GetString()!;
        var context = new Context();
        var map = new DependencyMap<string, int>(context);
        var recomputes = 0;
        var reader = context.Computed(ops =>
        {
            recomputes++;
            return map.ObserveDependency(wanted, ops);
        });
        Source<DependencyAvailability<int>>? identity = null;

        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.NotEmpty(steps);
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var op = step.GetProperty("op");
            switch (op.GetProperty("type").GetString())
            {
                case "observe_dependency":
                    context.Get(reader);
                    break;
                case "publish":
                    map.Publish(
                        op.GetProperty("key").GetString()!,
                        op.GetProperty("value").GetInt32());
                    break;
                case "unpublish":
                    map.Unpublish(op.GetProperty("key").GetString()!);
                    break;
                default:
                    Assert.Fail($"unsupported dependency operation: {op}");
                    break;
            }

            var state = context.Get(reader);
            Assert.True(map.TryGetHandle(wanted, out var current));
            identity ??= current;
            var expected =
                FixtureAssertions.Of(step, "expected", $"{fixture} step {index}");
            if (state.IsAvailable)
            {
                expected.AssertObjectKey(
                    "state",
                    available => available.AssertKey("Available", state.Value));
            }
            else
            {
                expected.AssertKey("state", "Unavailable");
            }

            expected.AssertKey("recomputes", recomputes);
            expected.AssertKey("present_count", map.PresentCount);
            Assert.Same(identity, current);
            expected.AssertKey("identity", "wanted-1");
            expected.Verify();
        }
    }

    [Fact]
    public async Task ThreadSafeAndAsyncFlavorsPreserveExactKeySourceIdentityAsync()
    {
        var threadContext = new ThreadSafeContext();
        var thread = new ThreadSafeDependencyMap<string, int>(threadContext);
        Assert.Equal(
            DependencyAvailability<int>.Unavailable,
            thread.ObserveDependency("wanted"));
        Assert.True(thread.TryGetHandle("wanted", out var threadHandle));
        thread.Publish("wanted", 7);
        Assert.True(thread.TryGetHandle("wanted", out var threadAfter));
        Assert.Same(threadHandle, threadAfter);
        Assert.Equal(
            DependencyAvailability<int>.Available(7),
            thread.ObserveDependency("wanted"));

        await using var asyncContext = new AsyncContext();
        var asyncMap = new AsyncDependencyMap<string, int>(asyncContext);
        Assert.Equal(
            DependencyAvailability<int>.Unavailable,
            asyncMap.ObserveDependency("wanted"));
        Assert.True(asyncMap.TryGetHandle("wanted", out var asyncHandle));
        asyncMap.Publish("wanted", 8);
        Assert.True(asyncMap.TryGetHandle("wanted", out var asyncAfter));
        Assert.Same(asyncHandle, asyncAfter);
        Assert.Equal(
            DependencyAvailability<int>.Available(8),
            asyncMap.ObserveDependency("wanted"));
    }
}
