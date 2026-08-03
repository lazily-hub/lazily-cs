using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lazily;
using Xunit;

namespace Lazily.Tests;

public sealed class BlobTransportConformanceTests
{
    [Fact]
    public void Canonical_arena_fixture_pins_descriptor_header_payload_and_round_trip()
    {
        using var document = SpecCorpus.Load("", "arena_blob.json");
        var root = document.RootElement;
        Assert.Equal("Arena", root.GetProperty("kind").GetString());
        var input = root.GetProperty("input");
        var expected = FixtureAssertions.Of(root, "expected", "arena_blob.json");
        var payload = input.GetProperty("payload")
            .EnumerateArray()
            .Select(value => value.GetByte())
            .ToArray();

        var arena = new ShmBlobArena(
            input.GetProperty("capacity").GetInt32(),
            input.GetProperty("epoch").GetUInt64());
        var descriptor = arena.Write(payload);

        // Descended rather than compared field-by-field (#lzsubblockkeyset): the child owns
        // the unconsumed-key teardown, so a sixth sub-field added upstream fails here instead
        // of being compared by nothing.
        expected.AssertObjectKey(
            "descriptor",
            want =>
            {
                want.AssertKey("offset", descriptor.Offset);
                want.AssertKey("len", descriptor.Length);
                want.AssertKey("generation", descriptor.Generation);
                want.AssertKey("epoch", descriptor.Epoch);
                want.AssertKey("checksum", descriptor.Checksum);
            });
        Assert.Null(descriptor.Backend);

        expected.AssertKey("header_bytes", arena.Bytes.Span[..ShmBlobArena.HeaderLength].ToArray());
        expected.AssertKey(
            "payload_region",
            arena.Bytes.Span.Slice(ShmBlobArena.HeaderLength, payload.Length).ToArray());
        Assert.Equal(payload, arena.Read(descriptor));
        expected.Verify();

        // The fixture's `assertions` block (#lznullformblind). Everything above replays
        // `input` against `expected`; this sibling block was carried by the same file and
        // read by NOTHING — not unread, unreachable, because no tracker ever saw it. Six
        // silent claims, one of them the header's magic.
        var assertions = FixtureAssertions.Of(root, "assertions", "arena_blob.json assertions");
        assertions.AssertKey("capacity", arena.Bytes.Length);
        assertions.AssertKey("epoch", descriptor.Epoch);
        assertions.AssertKey("payload_len", descriptor.Length);
        assertions.AssertKey("header_len", ShmBlobArena.HeaderLength);
        // Read out of the bytes the arena really wrote rather than off a constant:
        // comparing the magic to itself would pass over a writer that stopped emitting it.
        assertions.AssertKey(
            "magic",
            System.Text.Encoding.ASCII.GetString(arena.Bytes.Span[..4].ToArray().Reverse().ToArray()));
        // The KEY SET, not just the five fields — and owned by the TRACKER rather than by a
        // count written here (#lzsubblockkeyset). This site used to assert
        // `want.EnumerateObject().Count() == 5`, which is the same guarantee only for as
        // long as someone remembers to write it; descending makes an unrecognised sub-field
        // report as unconsumed with no call-site edit at all.
        assertions.AssertObjectKey(
            "descriptor",
            want =>
            {
                want.AssertKey("offset", descriptor.Offset);
                want.AssertKey("len", descriptor.Length);
                want.AssertKey("generation", descriptor.Generation);
                want.AssertKey("epoch", descriptor.Epoch);
                want.AssertKey("checksum", descriptor.Checksum);
            });
        assertions.Verify();
    }

    [Fact]
    public void Arena_rejects_oversized_stale_torn_and_old_epoch_descriptors()
    {
        var tooSmall = Assert.Throws<ShmBlobArenaException>(
            () => new ShmBlobArena(ShmBlobArena.HeaderLength));
        Assert.Equal(ShmBlobArenaError.CapacityTooSmall, tooSmall.Error);

        var smallArena = new ShmBlobArena(ShmBlobArena.HeaderLength + 4);
        var oversized = Assert.Throws<ShmBlobArenaException>(
            () => smallArena.Write([1, 2, 3, 4, 5]));
        Assert.Equal(ShmBlobArenaError.BlobTooLarge, oversized.Error);

        var wrapping = new ShmBlobArena((ShmBlobArena.HeaderLength * 2) + 8, epoch: 1);
        var old = wrapping.Write([1, 2, 3]);
        _ = wrapping.Write([4, 5, 6, 7]);
        _ = wrapping.Write([8, 9, 10]);
        Assert.False(wrapping.TryReadView(old, out _));

        var arena = new ShmBlobArena(ShmBlobArena.HeaderLength + 32, epoch: 4);
        var descriptor = arena.Write([11, 12, 13]);
        arena.DangerousBuffer[ShmBlobArena.HeaderLength] ^= 0xff;
        Assert.False(arena.TryReadView(descriptor, out _));

        var epochArena = new ShmBlobArena(ShmBlobArena.HeaderLength + 32);
        var priorEpoch = epochArena.Write([21, 22]);
        epochArena.AdvanceEpoch();
        Assert.False(epochArena.TryReadView(priorEpoch, out _));
    }

    [Fact]
    public void In_process_and_arrow_backends_obey_zero_copy_and_isolation_laws()
    {
        var inProcess = new InProcessBackend(512);
        var arrow = new ArrowBackend(512);
        var inProcessDescriptor = inProcess.Write([1, 2, 3]);
        var arrowDescriptor = arrow.Write([0x41, 0x52, 0x52, 0x4f, 0x57, 0x31]);

        Assert.Equal(BlobBackendKind.InProcess, inProcessDescriptor.Backend);
        Assert.Equal(BlobBackendKind.Arrow, arrowDescriptor.Backend);
        Assert.True(inProcess.TryReadView(inProcessDescriptor, out var firstView));
        Assert.True(inProcess.TryReadView(inProcessDescriptor, out var secondView));
        Assert.True(MemoryMarshal.TryGetArray(firstView, out var firstSegment));
        Assert.True(MemoryMarshal.TryGetArray(secondView, out var secondSegment));
        Assert.Same(firstSegment.Array, secondSegment.Array);
        Assert.Equal(firstSegment.Offset, secondSegment.Offset);
        Assert.Equal([1, 2, 3], firstView.ToArray());

        Assert.False(arrow.TryReadView(inProcessDescriptor, out _));
        Assert.False(
            inProcess.TryReadView(
                inProcessDescriptor with { Generation = inProcessDescriptor.Generation + 1 },
                out _));
        Assert.False(
            inProcess.TryReadView(
                inProcessDescriptor with { Checksum = inProcessDescriptor.Checksum + 1 },
                out _));

        var router = new BlobRouter().Register(inProcess).Register(arrow);
        Assert.True(router.TryReadView(inProcessDescriptor, out var routedInProcess));
        Assert.True(router.TryReadView(arrowDescriptor, out var routedArrow));
        Assert.Equal([1, 2, 3], routedInProcess.ToArray());
        Assert.Equal([0x41, 0x52, 0x52, 0x4f, 0x57, 0x31], routedArrow.ToArray());

        inProcess.AdvanceEpoch();
        Assert.False(inProcess.TryReadView(inProcessDescriptor, out _));
    }

    [Fact]
    public void Spill_policy_transforms_every_payload_site_and_router_resolves_original_bytes()
    {
        var backend = new InProcessBackend(4096);
        var large = Enumerable.Repeat((byte)0x5a, 128).ToArray();
        var small = new byte[] { 1, 2, 3 };
        var delta = new DeltaMessage(
            1,
            2,
            [
                new DeltaOp.CellSet(1, new IpcValue.Inline(large)),
                new DeltaOp.SlotValue(2, new IpcValue.Inline(small)),
                new DeltaOp.NodeAdd(3, "blob", new NodeState.Payload(large)),
                new DeltaOp.QueuePush(4, new IpcValue.Inline(large)),
            ]);

        var result = BlobTransport.SpillMessage(delta, backend, threshold: 64);
        Assert.Equal((ulong)(large.Length * 3), result.BytesSpilled);
        var spilledDelta = Assert.IsType<DeltaMessage>(result.Message);
        var cell = Assert.IsType<DeltaOp.CellSet>(spilledDelta.Ops[0]);
        var slot = Assert.IsType<DeltaOp.SlotValue>(spilledDelta.Ops[1]);
        var add = Assert.IsType<DeltaOp.NodeAdd>(spilledDelta.Ops[2]);
        var push = Assert.IsType<DeltaOp.QueuePush>(spilledDelta.Ops[3]);
        var cellBlob = Assert.IsType<IpcValue.SharedBlob>(cell.Payload);
        Assert.IsType<IpcValue.Inline>(slot.Payload);
        Assert.IsType<NodeState.SharedBlob>(add.State);
        Assert.IsType<IpcValue.SharedBlob>(push.Payload);

        var router = new BlobRouter().Register(backend);
        Assert.True(router.TryResolve(cellBlob, out var resolved));
        Assert.Equal(large, resolved.ToArray());

        var snapshot = new SnapshotMessage(
            2,
            [new NodeSnapshot(1, "blob", new NodeState.Payload(large))],
            [],
            [1]);
        var spilledSnapshot = BlobTransport.SpillMessage(snapshot, backend, threshold: 64);
        Assert.Equal((ulong)large.Length, spilledSnapshot.BytesSpilled);
        Assert.IsType<NodeState.SharedBlob>(
            Assert.IsType<SnapshotMessage>(spilledSnapshot.Message).Nodes[0].State);

        var sync = new CrdtSyncMessage(
            [new CrdtOp(1, null, new WireStamp(2, 0, 1), new IpcValue.Inline(large))]);
        var spilledSync = BlobTransport.SpillMessage(sync, backend, threshold: 64);
        Assert.Equal((ulong)large.Length, spilledSync.BytesSpilled);
        Assert.IsType<IpcValue.SharedBlob>(
            Assert.IsType<CrdtSyncMessage>(spilledSync.Message).Ops[0].State);
    }

    [Fact]
    public void Named_shm_backend_resolves_across_independent_mappings()
    {
        var name = $"cs-{Guid.NewGuid():N}";
        try
        {
            ShmBlobRef descriptor;
            using (var creator = ShmBackend.Create(name, 4096))
            using (var opener = ShmBackend.Open(name))
            {
                descriptor = creator.Write([9, 8, 7, 6]);
                creator.Flush();
                Assert.Equal(BlobBackendKind.Shm, descriptor.EffectiveBackend());
                Assert.Null(descriptor.Backend);
                Assert.True(opener.TryReadView(descriptor, out var view));
                Assert.Equal([9, 8, 7, 6], view.ToArray());
                Assert.False(
                    opener.TryReadView(
                        descriptor with { Backend = BlobBackendKind.Arrow },
                        out _));

                var concurrent = new ConcurrentBag<(ShmBlobRef Descriptor, byte[] Payload)>();
                Parallel.For(
                    0,
                    32,
                    index =>
                    {
                        var payload = BitConverter.GetBytes(index);
                        var backend = index % 2 == 0 ? creator : opener;
                        concurrent.Add((backend.Write(payload), payload));
                    });
                Assert.Equal(32, concurrent.Count);
                foreach (var item in concurrent)
                {
                    Assert.True(creator.TryReadView(item.Descriptor, out var creatorView));
                    Assert.True(opener.TryReadView(item.Descriptor, out var openerView));
                    Assert.Equal(item.Payload, creatorView.ToArray());
                    Assert.Equal(item.Payload, openerView.ToArray());
                }

                creator.AdvanceEpoch();
                Assert.False(opener.TryReadView(descriptor, out _));
            }
        }
        finally
        {
            ShmBackend.Unlink(name);
        }
    }
}
