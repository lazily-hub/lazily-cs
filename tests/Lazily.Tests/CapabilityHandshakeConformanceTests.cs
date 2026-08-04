using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

public sealed class CapabilityHandshakeConformanceTests
{
    private const string Corpus = "codec";
    private const string Fixture = "capability_handshake.json";

    [Fact]
    public void CanonicalScenariosNegotiateProductionSessionState()
    {
        using var fixture = SpecCorpus.Load(Corpus, Fixture);
        var root = fixture.RootElement;
        Assert.Equal("CapabilityHandshake", root.GetProperty("kind").GetString());

        foreach (var (_, id, scenarioView) in SpecCorpus.Scenarios(root, Corpus, Fixture).Indexed())
        {
            var scenario = scenarioView.Value;

            // Exercise both production codec directions before negotiation.
            var local = RoundTrip(scenario.GetProperty("local"));
            var remote = RoundTrip(scenario.GetProperty("remote"));
            var check = local.CheckCompatible(remote);
            var expected = FixtureAssertions.Of(scenario, "expected", $"{Fixture} {id}");

            expected.AssertKey("compatible", check.IsCompatible);
            if (expected.TryGetProperty("field", out _))
            {
                expected.AssertKey("field", check.Field);
            }

            if (check.IsCompatible)
            {
                var negotiated = new NegotiatedSession(local, remote);
                if (expected.TryGetProperty("negotiated_max_frame_size", out _))
                {
                    expected.AssertKey(
                        "negotiated_max_frame_size",
                        negotiated.MaxFrameSize);
                }
                if (expected.TryGetProperty(
                        "negotiated_fragmentation_supported",
                        out _))
                {
                    expected.AssertKey(
                        "negotiated_fragmentation_supported",
                        negotiated.FragmentationSupported);
                }
            }

            expected.Verify();
        }
    }

    private static SessionHandshake RoundTrip(JsonElement source)
    {
        var decoded = SessionHandshake.Deserialize(source.GetRawText());
        return SessionHandshake.Deserialize(decoded.Serialize());
    }
}
