using System.Diagnostics;
using System.Threading;

namespace Lazily;

/// <summary>An immutable snapshot of production-path lazily counters.</summary>
public sealed record LazilyMetricsSnapshot(
    long StateProjectionFramesApplied,
    long StateProjectionItemsApplied,
    long StateProjectionFramesIgnored,
    long StateProjectionGaps,
    long StateProjectionInvalidFrames,
    long StateMirrorFlushes,
    long CommandFramesRecorded,
    long CommandDuplicates,
    long CommandStaleFrames,
    long CommandTerminalReceipts,
    long CommandTerminalConflicts,
    long PermissionChecksAllowed,
    long PermissionChecksDenied,
    long PermissionGrantChanges,
    long HandshakesAccepted,
    long HandshakesRejected,
    long FfiFramesAccepted,
    long FfiFramesRejected);

/// <summary>
/// Lock-free counters incremented inside the state, command, permission, handshake, and FFI paths.
/// </summary>
public static class LazilyMetrics
{
    private static long _stateProjectionFramesApplied;
    private static long _stateProjectionItemsApplied;
    private static long _stateProjectionFramesIgnored;
    private static long _stateProjectionGaps;
    private static long _stateProjectionInvalidFrames;
    private static long _stateMirrorFlushes;
    private static long _commandFramesRecorded;
    private static long _commandDuplicates;
    private static long _commandStaleFrames;
    private static long _commandTerminalReceipts;
    private static long _commandTerminalConflicts;
    private static long _permissionChecksAllowed;
    private static long _permissionChecksDenied;
    private static long _permissionGrantChanges;
    private static long _handshakesAccepted;
    private static long _handshakesRejected;
    private static long _ffiFramesAccepted;
    private static long _ffiFramesRejected;

    /// <summary>Reads all counters atomically enough for diagnostics and tests.</summary>
    public static LazilyMetricsSnapshot Snapshot() =>
        new(
            Interlocked.Read(ref _stateProjectionFramesApplied),
            Interlocked.Read(ref _stateProjectionItemsApplied),
            Interlocked.Read(ref _stateProjectionFramesIgnored),
            Interlocked.Read(ref _stateProjectionGaps),
            Interlocked.Read(ref _stateProjectionInvalidFrames),
            Interlocked.Read(ref _stateMirrorFlushes),
            Interlocked.Read(ref _commandFramesRecorded),
            Interlocked.Read(ref _commandDuplicates),
            Interlocked.Read(ref _commandStaleFrames),
            Interlocked.Read(ref _commandTerminalReceipts),
            Interlocked.Read(ref _commandTerminalConflicts),
            Interlocked.Read(ref _permissionChecksAllowed),
            Interlocked.Read(ref _permissionChecksDenied),
            Interlocked.Read(ref _permissionGrantChanges),
            Interlocked.Read(ref _handshakesAccepted),
            Interlocked.Read(ref _handshakesRejected),
            Interlocked.Read(ref _ffiFramesAccepted),
            Interlocked.Read(ref _ffiFramesRejected));

    internal static void StateProjectionApplied(int nodesOrOperations, int edges)
    {
        Interlocked.Increment(ref _stateProjectionFramesApplied);
        Interlocked.Add(ref _stateProjectionItemsApplied, nodesOrOperations + edges);
    }

    internal static void StateProjectionIgnored() =>
        Interlocked.Increment(ref _stateProjectionFramesIgnored);

    internal static void StateProjectionGap() =>
        Interlocked.Increment(ref _stateProjectionGaps);

    internal static void StateProjectionInvalid() =>
        Interlocked.Increment(ref _stateProjectionInvalidFrames);

    internal static void StateMirrorFlushed(int operationCount)
    {
        Interlocked.Increment(ref _stateMirrorFlushes);
        Interlocked.Add(ref _stateProjectionItemsApplied, operationCount);
    }

    internal static void CommandFrameRecorded() =>
        Interlocked.Increment(ref _commandFramesRecorded);

    internal static void CommandDuplicate() =>
        Interlocked.Increment(ref _commandDuplicates);

    internal static void CommandStale() =>
        Interlocked.Increment(ref _commandStaleFrames);

    internal static void CommandTerminal() =>
        Interlocked.Increment(ref _commandTerminalReceipts);

    internal static void CommandConflict() =>
        Interlocked.Increment(ref _commandTerminalConflicts);

    internal static void PermissionAllowed() =>
        Interlocked.Increment(ref _permissionChecksAllowed);

    internal static void PermissionDenied() =>
        Interlocked.Increment(ref _permissionChecksDenied);

    internal static void PermissionGrantChanged() =>
        Interlocked.Increment(ref _permissionGrantChanges);

    internal static void HandshakeAccepted() =>
        Interlocked.Increment(ref _handshakesAccepted);

    internal static void HandshakeRejected() =>
        Interlocked.Increment(ref _handshakesRejected);

    internal static void FfiAccepted() =>
        Interlocked.Increment(ref _ffiFramesAccepted);

    internal static void FfiRejected() =>
        Interlocked.Increment(ref _ffiFramesRejected);
}

/// <summary>One deterministic benchmark measurement.</summary>
public sealed record BenchmarkResult(string Name, int Iterations, long ElapsedTicks)
{
    /// <summary>Average elapsed microseconds per iteration.</summary>
    public double AverageMicroseconds =>
        ElapsedTicks * 1_000_000d / Stopwatch.Frequency / Iterations;

    /// <summary>Measured operations per second.</summary>
    public double OperationsPerSecond =>
        Iterations * (double)Stopwatch.Frequency / Math.Max(1, ElapsedTicks);
}

/// <summary>A lightweight in-library benchmark harness for repeatable smoke measurements.</summary>
public static class LazilyBenchmark
{
    /// <summary>Runs one benchmark body for a positive number of iterations.</summary>
    public static BenchmarkResult Run(string name, int iterations, Action body)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        Guard.NotNull(body, nameof(body));
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));

        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) body();
        return new BenchmarkResult(name, iterations, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>Runs representative reactive-core and protocol-plane scenarios.</summary>
    public static IReadOnlyList<BenchmarkResult> RunSuite(int iterations = 10_000)
    {
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        return
        [
            Run(
                "source-read-write",
                iterations,
                () =>
                {
                    var context = new Context();
                    var source = context.Source(0);
                    source.Set(1);
                    _ = source.Get();
                }),
            Run(
                "state-projection-snapshot",
                iterations,
                () =>
                {
                    var projection = new StateProjection();
                    projection.ApplySnapshot(
                        new SnapshotMessage(
                            1,
                            [new NodeSnapshot(1, "i32", new NodeState.Payload([1]))],
                            [],
                            [1]));
                }),
            Run(
                "command-submit-fold",
                iterations,
                () =>
                {
                    var projection = new CommandProjection();
                    projection.Apply(
                        new CommandSubmit(
                            "cmd",
                            "cmd",
                            "source",
                            "target",
                            "bench",
                            "run",
                            1,
                            "key",
                            1_000,
                            new CommandPolicy(
                                DedupePolicy.SameCommandId,
                                Supersede: false,
                                CancelOnPreempt: true),
                            "bench.v1",
                            "sha256:bench",
                            new IpcValue.Inline([]),
                            []));
                }),
        ];
    }
}
