using System.Text.Json;
using Xunit;

namespace Lazily.Tests;

/// <summary>
/// The keyed-collection ordering contract replayed against every flavor that
/// exists in this binding.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CollectionsConformanceTests"/> already replays the ordering
/// fixtures against the single-threaded <c>SourceMap</c>. This suite extends
/// those laws to every supported execution flavor instead of treating one
/// passing flavor as family-wide coverage.
/// </para>
/// <para>
/// <c>ThreadSafeSourceMap</c> and <c>AsyncSourceMap</c> join the source-map
/// feature group only because they now expose real graph-backed entry,
/// membership, and order planes. The focused source-flavor test below pins
/// those planes directly. A future flavor that lacks one stays staged rather
/// than entering through a runner-local dictionary or lock.
/// </para>
/// <para>
/// Invalidation is measured by RECOMPUTE COUNT inside the reader's own compute
/// body, not by a cache flag: a counter the library has to move is the one probe
/// that cannot be satisfied by runner bookkeeping.
/// </para>
/// </remarks>
public sealed class CollectionsFamilyConformanceTests
{
    private const string Corpus = "collections";

    private static readonly string[] Fixtures =
    [
        "cellmap_atomic_move.json",
        "cellmap_independence.json",
    ];

    /// <summary>Order-sensitive, so an order reader's VALUE changes on a reorder.</summary>
    private static int OrderDigest(IReadOnlyList<string> keys)
    {
        var acc = 17;
        foreach (var key in keys)
        {
            foreach (var ch in key) acc = (acc * 31) + ch;
            acc = (acc * 31) + 7;
        }

        return acc;
    }

    /// <summary>Replays both canonical ordering fixtures against the single-threaded map.</summary>
    [Fact]
    public void OrderingContractHoldsOnTheSingleThreadedMap()
    {
        Assert.NotNull(SpecCorpus.Root);

        foreach (var fixtureName in Fixtures)
        {
            using var doc = SpecCorpus.Load(Corpus, fixtureName);
            var root = doc.RootElement;
            var ctx = new Context();
            var map = new SourceMap<string, int>(ctx);

            var initial = root.GetProperty("initial");
            var seed = initial.GetProperty("order").EnumerateArray().Select(k => k.GetString()!).ToList();
            Assert.NotEmpty(seed);
            var values = initial.GetProperty("values");
            foreach (var key in seed) map.Set(key, values.GetProperty(key).GetInt32());

            var steps = root.GetProperty("steps").EnumerateArray().ToList();

            // A zero-step replay asserts nothing and still reports green.
            Assert.NotEmpty(steps);

            var matrices = 0;

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var op = step.GetProperty("op");
                var where = $"{fixtureName} step {i}";
                var expected = FixtureAssertions.Of(step, "expected", where);

                // Rebuild + settle readers from the CURRENT key set so each step's
                // invalidation is measured against a fully settled graph.
                var beforeKeys = map.PresentKeys().ToList();
                var valueCounts = new Dictionary<string, int>();
                var valueReaders = new Dictionary<string, Computed<int>>();
                foreach (var key in beforeKeys)
                {
                    var k = key;
                    valueCounts[k] = 0;
                    valueReaders[k] = ctx.Computed(ops =>
                    {
                        valueCounts[k]++;
                        return map.TryGetHandle(k, out var h) ? ops.Get(h) : -1;
                    });
                }

                var membershipCount = 0;
                var membership = ctx.Computed(ops =>
                {
                    membershipCount++;
                    return map.Len(ops);
                });
                var orderCount = 0;
                var order = ctx.Computed(ops =>
                {
                    orderCount++;
                    return OrderDigest(map.Keys(ops));
                });

                foreach (var reader in valueReaders.Values) ctx.Get(reader);
                ctx.Get(membership);
                ctx.Get(order);
                var valueBase = valueCounts.ToDictionary(kv => kv.Key, kv => kv.Value);
                var membershipBase = membershipCount;
                var orderBase = orderCount;

                var handlesBefore = new Dictionary<string, object?>();
                foreach (var key in beforeKeys)
                {
                    handlesBefore[key] = map.TryGetHandle(key, out var h) ? h : null;
                }

                switch (op.GetProperty("type").GetString())
                {
                    case "set_value":
                    case "insert":
                        {
                            var key = op.GetProperty("key").GetString()!;
                            map.Set(key, op.GetProperty("value").GetInt32());

                            // `at` says where the new key lands; minting appends, so
                            // "end" is already right. An unrecognised form must fail,
                            // not silently append.
                            if (op.TryGetProperty("at", out var at))
                            {
                                if (at.ValueKind == JsonValueKind.Number) map.MoveTo(key, at.GetInt32());
                                else Assert.Equal("end", at.GetString());
                            }

                            break;
                        }

                    case "remove":
                        map.Remove(op.GetProperty("key").GetString()!);
                        break;
                    case "move_to":
                        map.MoveTo(op.GetProperty("key").GetString()!, op.GetProperty("index").GetInt32());
                        break;
                    case "move_before":
                        map.MoveBefore(
                            op.GetProperty("key").GetString()!,
                            op.GetProperty("before").GetString()!);
                        break;
                    case "move_after":
                        map.MoveAfter(
                            op.GetProperty("key").GetString()!,
                            op.GetProperty("after").GetString()!);
                        break;
                    default:
                        Assert.Fail($"{where}: unsupported op - an unknown op must fail, never skip");
                        break;
                }

                var gotOrder = map.PresentKeys().ToList();
                Assert.Equal(
                    expected.GetProperty("order").EnumerateArray().Select(k => k.GetString()!).ToList(),
                    gotOrder);

                if (expected.TryGetProperty("values", out var wantValues))
                {
                    foreach (var want in wantValues.EnumerateObject())
                    {
                        Assert.True(map.TryGetHandle(want.Name, out var h), $"{where}: {want.Name} absent");
                        Assert.Equal(want.Value.GetInt32(), ctx.Get(h));
                    }
                }

                // The invalidation matrix, read from expected.invalidates - where
                // the fixtures actually nest it. lazily-rs read it off the step
                // instead, so its assertion never ran once.
                Assert.True(
                    expected.TryGetProperty("invalidates", out var invalidates),
                    $"{where}: expected.invalidates is missing - the matrix is the contract");
                matrices++;

                var dirty = invalidates.TryGetProperty("value", out var dv)
                    ? dv.EnumerateArray().Select(k => k.GetString()!).ToHashSet()
                    : [];
                var survivors = gotOrder.ToHashSet();
                foreach (var (key, reader) in valueReaders)
                {
                    if (!survivors.Contains(key)) continue; // removed: nothing left to read
                    ctx.Get(reader);
                    var recomputed = valueCounts[key] != valueBase[key];
                    if (dirty.Contains(key))
                    {
                        Assert.True(recomputed, $"{where}: value reader for {key} should have been invalidated");
                    }
                    else
                    {
                        Assert.False(
                            recomputed,
                            $"{where}: value reader for {key} should have stayed cached - " +
                            "per-entry independence is the whole point");
                    }
                }

                ctx.Get(membership);
                ctx.Get(order);
                Assert.Equal(
                    invalidates.TryGetProperty("membership", out var m) && m.GetBoolean(),
                    membershipCount != membershipBase);
                Assert.Equal(
                    invalidates.TryGetProperty("order", out var o) && o.GetBoolean(),
                    orderCount != orderBase);

                // `membership` is the ORDER-INDEPENDENT key set. It is not implied by
                // `order`: a shell that dropped a key and re-appended it lands the same
                // ordered list only if the drop and the append cancel, and the membership
                // set is what says whether the key was ever absent.
                if (expected.TryGetProperty("membership", out var wantMembership))
                {
                    Assert.Equal(
                        wantMembership.EnumerateArray()
                            .Select(value => value.GetString()!)
                            .Order(StringComparer.Ordinal),
                        map.PresentKeys().Order(StringComparer.Ordinal));
                }

                // Handle stability: the law separating an atomic move from a
                // remove + re-mint. A reorder keeps the entry's node.
                if (expected.TryGetProperty("handle_stable", out var stable))
                {
                    foreach (var entry in stable.EnumerateObject())
                    {
                        var after = map.TryGetHandle(entry.Name, out var h) ? h : null;
                        handlesBefore.TryGetValue(entry.Name, out var before);
                        if (entry.Value.GetBoolean())
                        {
                            Assert.NotNull(before);
                            Assert.Same(before, after);
                        }
                        else
                        {
                            Assert.NotSame(before, after);
                        }
                    }
                }

                expected.Verify();
            }

            Assert.True(matrices > 0, $"{fixtureName}: asserted no invalidation matrix");
        }
    }

    /// <summary>
    /// Every supported source-map peer carries independent value, membership,
    /// and order reader planes.
    /// </summary>
    [Fact]
    public async Task SourceFlavorsCarryExactReaderPlanesAsync()
    {
        {
            var ctx = new ThreadSafeContext();
            var map = new ThreadSafeSourceMap<string, int>(ctx);
            map.Set("a", 1);
            map.Set("b", 2);
            map.Set("c", 3);

            var valueRuns = 0;
            var value = ctx.WithLock(inner => inner.Computed(ops =>
            {
                valueRuns++;
                return map.TryGetHandle("a", out var handle) ? ops.Get(handle) : -1;
            }));
            var lenRuns = 0;
            var len = ctx.WithLock(inner => inner.Computed(ops =>
            {
                lenRuns++;
                return map.Len(ops);
            }));
            var orderRuns = 0;
            var order = ctx.WithLock(inner => inner.Computed(ops =>
            {
                orderRuns++;
                return OrderDigest(map.Keys(ops));
            }));
            int Read(Computed<int> reader) => ctx.WithLock(inner => inner.Get(reader));

            Assert.Equal(1, Read(value));
            Assert.Equal(3, Read(len));
            Read(order);
            Assert.Equal((1, 1, 1), (valueRuns, lenRuns, orderRuns));
            Assert.True(map.TryGetHandle("a", out var stable));

            map.Set("a", 10);
            Assert.Equal(10, Read(value));
            Read(len);
            Read(order);
            Assert.Equal((2, 1, 1), (valueRuns, lenRuns, orderRuns));

            Assert.True(map.MoveBefore("a", "c"));
            Read(value);
            Read(len);
            Read(order);
            Assert.Equal((2, 1, 2), (valueRuns, lenRuns, orderRuns));

            map.Set("d", 4);
            Read(value);
            Assert.Equal(4, Read(len));
            Read(order);
            Assert.Equal((2, 2, 3), (valueRuns, lenRuns, orderRuns));

            var removedRuns = 0;
            var removed = ctx.WithLock(inner => inner.Computed(ops =>
            {
                removedRuns++;
                return map.TryGetHandle("b", out var handle) ? ops.Get(handle) : -1;
            }));
            Assert.Equal(2, Read(removed));
            Assert.Equal(1, removedRuns);
            Assert.True(map.Remove("b"));
            Read(value);
            Assert.Equal(3, Read(len));
            Read(order);
            Assert.Equal((2, 3, 4), (valueRuns, lenRuns, orderRuns));
            Assert.Equal(-1, Read(removed));
            Assert.Equal(2, removedRuns);
            Assert.True(map.TryGetHandle("a", out var after));
            Assert.Same(stable, after);
        }

        {
            await using var ctx = new AsyncContext();
            var map = new AsyncSourceMap<string, int>(ctx);
            map.Set("a", 1);
            map.Set("b", 2);
            map.Set("c", 3);

            var valueRuns = 0;
            var value = ctx.Computed(compute =>
            {
                valueRuns++;
                return Task.FromResult(
                    map.TryObserve("a", out var observed, compute) ? observed : -1);
            });
            var lenRuns = 0;
            var len = ctx.Computed(compute =>
            {
                lenRuns++;
                return Task.FromResult(map.Len(compute));
            });
            var orderRuns = 0;
            var order = ctx.Computed(compute =>
            {
                orderRuns++;
                return Task.FromResult(OrderDigest(map.Keys(compute)));
            });

            Assert.Equal(1, await value.GetAsync());
            Assert.Equal(3, await len.GetAsync());
            await order.GetAsync();
            Assert.Equal((1, 1, 1), (valueRuns, lenRuns, orderRuns));
            Assert.True(map.TryGetHandle("a", out var stable));

            map.Set("a", 10);
            Assert.Equal(10, await value.GetAsync());
            await len.GetAsync();
            await order.GetAsync();
            Assert.Equal((2, 1, 1), (valueRuns, lenRuns, orderRuns));

            Assert.True(map.MoveBefore("a", "c"));
            await value.GetAsync();
            await len.GetAsync();
            await order.GetAsync();
            Assert.Equal((2, 1, 2), (valueRuns, lenRuns, orderRuns));

            map.Set("d", 4);
            await value.GetAsync();
            Assert.Equal(4, await len.GetAsync());
            await order.GetAsync();
            Assert.Equal((2, 2, 3), (valueRuns, lenRuns, orderRuns));

            Assert.True(map.TryGetHandle("b", out var doomed));
            Assert.True(map.Remove("b"));
            await value.GetAsync();
            Assert.Equal(3, await len.GetAsync());
            await order.GetAsync();
            Assert.Equal((2, 3, 4), (valueRuns, lenRuns, orderRuns));
            Assert.Throws<DisposedNodeException>(() => doomed.Peek());
            Assert.True(map.TryGetHandle("a", out var after));
            Assert.Same(stable, after);
        }
    }

    /// <summary>
    /// The derived-slot flavors carry the Core surface too.
    /// </summary>
    /// <remarks>
    /// A ComputedMap has no Set, so the value-mutating fixtures cannot drive it.
    /// That is exactly how the thread-safe and async maps ended up with no
    /// ordering surface while the matrix read green, so they are driven directly
    /// against the same laws here.
    /// </remarks>
    [Fact]
    public async Task DerivedSlotFlavorsCarryTheCoreSurfaceAsync()
    {
        string[] seed = ["a", "b", "c", "d"];

        {
            var ctx = new Context();
            var map = new ComputedMap<string, int>(ctx);
            for (var i = 0; i < seed.Length; i++)
            {
                var v = i + 1;
                map.GetOrInsertWith(seed[i], _ => v);
            }

            var orderCount = 0;
            var order = ctx.Computed(ops =>
            {
                orderCount++;
                return OrderDigest(map.Keys(ops));
            });
            var lenCount = 0;
            var len = ctx.Computed(ops =>
            {
                lenCount++;
                return map.Len(ops);
            });
            ctx.Get(order);
            ctx.Get(len);
            var orderBase = orderCount;
            var lenBase = lenCount;
            map.TryGetHandle("a", out var handleBefore);

            Assert.True(map.MoveBefore("a", "d"));
            Assert.Equal(new[] { "b", "c", "a", "d" }, map.PresentKeys());
            ctx.Get(order);
            ctx.Get(len);
            Assert.NotEqual(orderBase, orderCount);
            Assert.Equal(lenBase, lenCount); // a pure reorder must NOT touch membership
            Assert.True(map.TryGetHandle("a", out var handleAfter));
            Assert.Same(handleBefore, handleAfter);
        }

        {
            var ctx = new ThreadSafeContext();
            var map = new ThreadSafeComputedMap<string, int>(ctx);
            for (var i = 0; i < seed.Length; i++)
            {
                var v = i + 1;
                map.GetOrInsertWith(seed[i], _ => v);
            }

            Assert.True(map.MoveBefore("a", "d"));
            Assert.Equal(new[] { "b", "c", "a", "d" }, map.PresentKeys());
            Assert.True(map.TryPosition("a", out var at));
            Assert.Equal(2, at);
        }

        {
            var ctx = new AsyncContext();
            var map = new AsyncComputedMap<string, int>(ctx);
            for (var i = 0; i < seed.Length; i++)
            {
                var v = i + 1;
                map.MaterializeAll([seed[i]], _ => v);
            }

            // Assert the ORDER SIGNAL, not just the list: a map that reorders
            // its list without bumping would still pass a list-only check, which
            // is how a non-reactive ordering surface reads green.
            var orderCount = 0;
            var order = ctx.Computed(compute =>
            {
                orderCount++;
                return Task.FromResult(OrderDigest(map.Keys(compute)));
            });
            var lenCount = 0;
            var len = ctx.Computed(compute =>
            {
                lenCount++;
                return Task.FromResult(map.Len(compute));
            });
            await order.GetAsync();
            await len.GetAsync();
            var orderBase = orderCount;
            var lenBase = lenCount;

            Assert.True(map.MoveBefore("a", "d"));
            Assert.Equal(new[] { "b", "c", "a", "d" }, map.PresentKeys());
            Assert.True(map.TryPosition("a", out var at));
            Assert.Equal(2, at);

            await order.GetAsync();
            await len.GetAsync();
            Assert.NotEqual(orderBase, orderCount);
            Assert.Equal(lenBase, lenCount); // a pure reorder must NOT touch membership

            // And a POSITIVE membership assertion. "Did not invalidate" passes
            // trivially for a reader that tracks nothing, which is exactly what
            // this map's Len was: the membership cell was written on every
            // mutation and never read anywhere.
            lenBase = lenCount;
            Assert.True(map.TryGetHandle("b", out var doomed));
            Assert.True(map.Remove("b"));
            await len.GetAsync();
            Assert.NotEqual(lenBase, lenCount);
            Assert.Equal(3, map.PresentCount);

            // The removed node must be torn down. Without this a reader still
            // holding the handle keeps being served the removed entry's last
            // resolved value — this map was the one flavor whose Remove did not
            // dispose.
            await Assert.ThrowsAsync<DisposedNodeException>(() => doomed.GetAsync());
        }
    }

    /// <summary>
    /// Covers a direction the canonical corpus does not.
    /// </summary>
    /// <remarks>
    /// cellmap_atomic_move.json's only move_before step moves a key that already
    /// FOLLOWS its anchor (from=2, anchor=0), so it exercises only the branch
    /// where the insertion point is the anchor index itself. The branch where the
    /// key PRECEDES its anchor — target anchor-1 — is never replayed. That is
    /// exactly the direction lazily-zig's moveBefore was wrong in.
    /// </remarks>
    [Fact]
    public void DirectionalMovesBindEveryFlavor()
    {
        string[] seed = ["a", "b", "c", "d"];

        SourceMap<string, int> Build()
        {
            var map = new SourceMap<string, int>(new Context());
            for (var i = 0; i < seed.Length; i++) map.Set(seed[i], i + 1);
            return map;
        }

        (string What, Action<SourceMap<string, int>> Run, string[] Want)[] cases =
        [
            ("move_before, key precedes anchor", m => m.MoveBefore("a", "d"), ["b", "c", "a", "d"]),
            ("move_before, key follows anchor", m => m.MoveBefore("d", "b"), ["a", "d", "b", "c"]),
            ("move_after, key precedes anchor", m => m.MoveAfter("a", "c"), ["b", "c", "a", "d"]),
            ("move_after, key follows anchor", m => m.MoveAfter("d", "a"), ["a", "d", "b", "c"]),
            ("move_to past the end clamps", m => m.MoveTo("a", 99), ["b", "c", "d", "a"]),
            ("move_to to -1 clamps to the front", m => m.MoveTo("d", -1), ["d", "a", "b", "c"]),
            ("move on an absent key is a no-op", m =>
            {
                m.MoveBefore("zz", "a");
                m.MoveTo("zz", 0);
            }, seed),
        ];

        foreach (var (what, run, want) in cases)
        {
            var map = Build();
            map.TryGetHandle("a", out var before);
            run(map);
            Assert.Equal(want, map.PresentKeys());
            Assert.True(map.TryGetHandle("a", out var after));
            Assert.Same(before, after);
        }

        // A no-op move names a present key, so it APPLIES even though it changes
        // nothing. This binding used to report false there, alone in the family.
        var noop = Build();
        Assert.True(noop.MoveTo("a", 0), "a no-op move on a present key still applies");
    }
}
