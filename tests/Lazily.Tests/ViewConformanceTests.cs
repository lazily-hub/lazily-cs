using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

public sealed class ViewConformanceTests
{
    private static readonly string[] Fixtures =
    [
        "batch_send.json",
        "counter.json",
        "field_change.json",
        "malformed_rejected.json",
    ];

    [Fact]
    public void ExposesTheHostApiToAssembliesWithoutFriendAccess()
    {
        var type = typeof(View);
        Assert.True(type.IsPublic);
        foreach (var method in new[] { "Install", "SignalsJson", "ExpressionsJson", "LayoutJson", "ChartJson", "DispatchJson" })
            Assert.NotNull(type.GetMethod(method));
    }

    [Fact]
    public void ReplaysTheViewCorpusWhenTheAreaExists()
    {
        var names = SpecCorpus.FixtureNames("view");
        if (names.Count == 0) return;
        Assert.Equal(Fixtures, names);
        foreach (var name in names) Replay(name);
    }

    private static void Replay(string name)
    {
        using var doc = SpecCorpus.Load("view", name);
        var root = doc.RootElement;
        if (root.GetProperty("kind").GetString() == "ViewMalformed")
        {
            foreach (var scenario in root.GetProperty("cases").EnumerateArray())
            {
                var label = scenario.GetProperty("name").GetString();
                Assert.ThrowsAny<Exception>(() =>
                {
                    var ctx = new Context();
                    _ = View.Install(ctx, scenario.GetProperty("view"));
                });
                Assert.False(string.IsNullOrEmpty(label));
            }

            return;
        }

        var context = new Context();
        var view = View.Install(context, root.GetProperty("view"));
        _ = view.Subscription;
        Assert.Empty(root.GetProperty("trace").EnumerateArray());
        AssertSame(root.GetProperty("signals"), view.SignalsJson());
        AssertSame(root.GetProperty("expressions"), view.ExpressionsJson());
        AssertSame(root.GetProperty("layout"), view.LayoutJson());
        AssertSame(root.GetProperty("chart"), view.ChartJson());
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var trace = view.DispatchJson(
                step.GetProperty("node").GetString()!,
                step.GetProperty("event").GetString()!,
                step.TryGetProperty("value", out var value) ? value : default);
            AssertSame(step.GetProperty("signals"), view.SignalsJson());
            AssertSame(step.GetProperty("expressions"), view.ExpressionsJson());
            AssertSame(step.GetProperty("trace"), trace);
            AssertSame(step.GetProperty("chart"), view.ChartJson());
        }
    }

    private static void AssertSame(JsonElement want, object? got)
    {
        Assert.Equal(Norm(want), Norm(got), StringComparer.Ordinal);
    }

    private static string Norm(object? value) => JsonSerializer.Serialize(Canon(value));

    private static object? Canon(object? value) => value switch
    {
        null => null,
        JsonElement el => CanonElement(el),
        Dictionary<string, object?> map => map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => Canon(pair.Value), StringComparer.Ordinal),
        IEnumerable<string> strings when value is not string => strings.ToList(),
        IEnumerable<object?> list when value is not string => list.Select(Canon).ToList(),
        _ => value,
    };

    private static object? CanonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt64(out var n) => n,
        JsonValueKind.Array => el.EnumerateArray().Select(CanonElement).ToList(),
        JsonValueKind.Object => el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => CanonElement(p.Value), StringComparer.Ordinal),
        _ => el.GetRawText(),
    };
}
