# lazily-cs

Lazy reactive primitives for C#/.NET — `Source`, `Computed`, and `Effect` with automatic
dependency tracking, plus the [lazily-spec](https://github.com/lazily-hub/lazily-spec) wire
protocol, CRDTs, and distributed plane.

This is the ninth binding in the lazily family. The reactive kernel is a direct port of the
reference semantics, and it replays the shared cross-language conformance corpus rather than
asserting behaviour it invented locally.

> **Status: early.** The reactive graph and the merge algebra are shipped and spec-conformant.
> The remaining planes named in the feature matrix below are not implemented yet — the matrix is
> the honest, single-source record of what exists.

## Install

```bash
dotnet add package Lazily
```

Targets `net10.0`.

## Quick start

```csharp
using Lazily;

var ctx = new Context();

var celsius = ctx.Source(21.0);
var fahrenheit = ctx.Computed<double>(c => celsius.Get(c) * 9 / 5 + 32);

Console.WriteLine(fahrenheit.Get()); // 69.8 — computed on first read, then cached

using var log = ctx.Effect(c =>
{
    Console.WriteLine($"now {fahrenheit.Get(c):F1}F");
    return null; // an optional cleanup callback
});

celsius.Set(25.0); // the effect reruns; fahrenheit recomputes on demand
```

## The model

**Two cell kinds, one read surface, one write surface.**

- `Source<T>` is a value written from outside the graph. It is the only kind carrying
  `Set`/`Merge`, so write protection lives in the type rather than in a runtime gate.
- `Computed<T>` derives a value from upstream. It is lazy and cached, and **guarded** by default:
  a recompute yielding an equal value suppresses the whole downstream cascade.
- `Effect` is the only sink. There are no observers, no subscriptions, and no change callbacks on
  a handle — if you want to react to something, you write an effect.

**Tracking is value-threaded, never ambient.** A compute body receives a `Compute` view carrying
the recomputing node's identity as a value:

```csharp
var total = ctx.Computed<int>(c => a.Get(c) + b.Get(c));   // tracked: forms edges
var once  = ctx.Computed<int>(c => a.Get(c.Untracked()));  // untracked: forms none
```

There is no ambient recompute stack to read from, so `Untracked()` is genuinely untracked and a
read outside a compute cannot accidentally attribute an edge to whatever ran last. C# cannot bind
the view to its recompute the way a lifetime does, so escape is caught at runtime: using a stored
view after its compute returned throws `StaleComputeException` instead of silently registering an
edge against a node that is no longer recomputing.

**Invalidation is a non-consuming mark-frontier walk.** A write marks its transitive dependent
cone stale and leaves every edge in place; nothing recomputes until something is read. Because the
edges survive, a node can be marked clean again *without* recomputing and still be reachable from
its source — which is what keeps the next genuine change from being lost at depth two.

**Batching coalesces the cascade, never the algebra.**

```csharp
ctx.Batch(() =>
{
    acc.Merge(1);
    acc.Merge(2);
    acc.Merge(3);
});
// three folds happened synchronously; the watcher ran once
```

**Eager is a state, not a kind.** `computed.Eager()` attaches a puller effect that materializes
the value immediately and again after every invalidation. Because the puller is an ordinary
effect, N invalidations inside a batch coalesce into one pull at the flush. `Lazy()` reverses it.

**Disposal is explicit, and it dirties what survives.** Dropping the last C# reference to a node
reclaims nothing reactive: the graph holds strong edges, so a long-lived source retains every node
that ever read it. `Dispose()` detaches both edge directions and marks the surviving cone stale.
A `TeardownScope` groups nodes and tears them down in reverse creation order:

```csharp
var scope = ctx.Scope();
var view = scope.Own(ctx.Computed<int>(c => source.Get(c) * 2));
scope.Close();   // reverse creation order; effect cleanups run
```

**A divergent feedback loop reports exhaustion instead of hanging.** An effect that writes into
its own dependency cone closes a loop through the *scheduler*, not the graph — it is not a
dependency cycle, and it runs flat at constant stack depth, so neither acyclicity nor recursion
bounds can catch it. `Context.DrainBudget` is the only exit, and `LastDrainExhaustion` identifies
the effect that concentrated the runs rather than merely announcing that a counter was hit.

## Concurrency layers

.NET has real threads, so both concurrency layers are required of this binding rather than
declared `none`.

**`ThreadSafeContext` — lock-backed.** It wraps a single-threaded `Context` with a reentrant lock
and reuses the core batch coalescing, so it *refines* the kernel rather than reimplementing it: a
one-write critical section is observationally a plain `Set`, and a batch of concurrent writes
coalesces into one invalidation pass whose result is a function of the serialized write list, not
of the interleaving the lock happened to pick.

```csharp
var ts = new ThreadSafeContext();
Source<int> total = null!;
ts.WithLock(ctx => total = ctx.Source(0));

ts.Batch(() =>          // three writes, one coalesced cascade
{
    ts.Set(total, 1);
    ts.Set(total, 2);
    ts.Set(total, 3);
});

var now = ts.WithLock(_ => total.Peek());
```

`ThreadSafeKernel.ApplyBatch` / `FlushBatch` are the pure counterpart of the Lean
`LazilyFormal.ThreadSafe` model — the coalescing law over a plain node table, checkable without a
live graph.

**`AsyncContext` — a distinct graph.** It is not an overload of the other two. An async slot can
be *in flight* when its inputs change, can complete after those inputs are gone, and can be
cancelled mid-flight, so it carries an explicit state machine (`Empty` / `Computing` / `Resolved`
/ `Error`), a revision per slot, and its own handles. Sources stay the **synchronous input layer**:
`Source`, `Peek`, and `Set` are synchronous; only computed evaluation and effects are async.

```csharp
await using var ctx = new AsyncContext();
var userId = ctx.Source(1);
var profile = ctx.Computed(async cc => await FetchAsync(cc.Track(userId), cc.Token));

Console.WriteLine(await profile.GetAsync());
userId.Set(2);                       // supersedes the in-flight compute
Console.WriteLine(await profile.GetAsync());
```

The contract it honours in full:

- **Revision tracking discards every stale completion.** A run publishes only while the slot still
  holds the token it started with, so a value the graph has already moved past is never served.
- **Dropping one waiter cancels only that waiter.** The shared computation keeps running for the
  readers that remain, and there is at most one in flight per revision — concurrent readers attach
  rather than spawning duplicates.
- **`GetAsync` re-resolves rather than asserting.** The slot can change between lock acquisitions
  and a superseded run closes its waiters without a value; both windows are benign and neither
  throws.
- **Effect cleanup runs on rerun or dispose and at no other time** — never at the end of the flush
  that ran the body. The canonical effect acquires in the body and releases in the cleanup, so a
  flush-end cleanup would release while the effect is still live. Reruns are serialized: the next
  body does not start until the previous cleanup completes.
- **Disposal awaits.** Disposing the context cancels every in-flight computation and awaits every
  active cleanup before returning.
- **`Batch` is synchronous at the mutation boundary.** Writes queue their roots; async reruns fire
  after the outermost batch exits, never inside it.

## Merge algebra

A `Source<T>` folds writes under a `MergePolicy<T>`; the default is keep-latest, so a plain source
*is* a plain cell (`Cell ≡ Source<KeepLatest>`). Associativity is a law, verified by the law tests.
The flags are declarations about which overflow behaviour is sound downstream: commutativity is
the reordering tax, idempotency the durability tax, and only raw FIFO cannot conflate.

| Policy | Fold | Commutative | Idempotent | Conflates |
|---|---|:---:|:---:|:---:|
| `KeepLatest` | `op` | — | ✅ | ✅ |
| `Sum` | `a + b` | ✅ | — | ✅ |
| `Max` | `max(a, b)` | ✅ | ✅ | ✅ |
| `SetUnion` | `a ∪ b` | ✅ | ✅ | ✅ |
| `RawFifo` | `a ++ b` | — | — | — |

The write guard runs on the merged result, so an idempotent policy's no-op merge fires no cascade.

## Divergences from the reference bindings

- **No `comparable` bound.** Rust and Go bound a source's value so the write guard can use `==`.
  C# has no such bound, so the guard uses `EqualityComparer<T>.Default` and every constructor
  accepts an explicit `IEqualityComparer<T>`. That is strictly more general, and it means a
  reference type without a value-equality override is guarded by reference identity unless you
  pass a comparer.
- **Constructors are extension methods.** `ctx.Source(…)`, `ctx.Computed(…)`, `ctx.Slot(…)`, and
  `ctx.Effect(…)` live on `Reactive` so the factory names can match the family vocabulary without
  colliding with the type names they return.
- **`AsyncContext` serializes on a lock, not an owner loop.** lazily-go funnels every graph
  mutation through one goroutine and a command channel; `Monitor` is reentrant, so the same
  invariant is expressed directly as a lock. Bodies run off it on the thread pool.
- **An async slot in `Error` retries on the next read.** lazily-go serves the stored error
  forever; `docs/async.md` lists `Error → Computing` on retry, and the spec is the authority.

## Conformance

The cross-language corpus lives in [lazily-spec](https://github.com/lazily-hub/lazily-spec) and is
**never vendored here** — a bundled copy drifts from the spec. Clone it beside this repo:

```bash
git clone https://github.com/lazily-hub/lazily-spec.git ../lazily-spec
make check
```

The runner fails hard when the corpus is absent rather than skipping, asserts a positive fixture
and assertion count, and keeps an explicit ledger of unsupported fixtures and known divergences —
both are asserted to match exactly, so a new divergence fails the build and a fixed one fails it
until its entry is deleted. Today lazily-cs replays **the whole `reactive-graph` corpus with an
empty ledger**, including the merge-feed and bounded-feedback-drain fixtures.

The runner is parameterised over the **execution model** and replays the same op stream against
`Context`, `ThreadSafeContext`, and `AsyncContext`. That is not thoroughness for its own sake: a
cascade that stops one level below the write is correct synchronously and broken asynchronously,
because an async read short-circuits on a resolved cache and serves the stale value forever. A
single-context replay cannot see it. Constructs one plane does not ship (the eager `signal`, the
`merge_cell` fold, the bounded drain — all synchronous-kernel constructs) are gated per model with
a stated reason, never degraded to the nearest available substitute.

## Feature coverage

Generated from `coverage.json` in lazily-spec — do not edit by hand.

<!-- coverage-table:start -->
| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reactive graph — two cell kinds (nodes `SourceCell` / `ComputedCell`; handles `Source<T, M>` / `Computed<T>`) + `Effect` sink + eager `Computed` (`computed().eager()`) / all cells guarded / batch | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Keyed-map materialization (`ComputedMap`) — mint-on-access derived slots: transparency + deferral (`#lzmatmode`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Thread-safe keyed map (`ThreadSafeComputedMap`) — `Send + Sync` + materialization confluence (`#lzmatmode`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Async keyed map (`AsyncComputedMap`) — eventual transparency (`#lzmatmode`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Keyed-map sync — membership propagation + materialize-on-ingest + derived-aggregate transparency (`#lzfamilysync`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Thread-safe context (lock-backed) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Async reactive context | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Flat state machine | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Harel state charts | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Keyed reactive maps (`ReactiveMap`: `SourceMap` / `ComputedMap`) + `SourceTree` + reconcile | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `ReactiveMap` **Core surface** — single-threaded flavor (cell-model.md § Core surface vs. binding extensions) | ✅ | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ |
| `ReactiveMap` **Core surface** — thread-safe flavor (ordering + membership reactivity) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ~ |
| `ReactiveMap` **Core surface** — async flavor (ordering + membership reactivity) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ~ |
| Atomic ordered move replayed against **all three flavors** (`cellmap_atomic_move` + `cellmap_independence`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ~ |
| Memoized semantic tree (`SemTree`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Stable-id alignment (manufactured identity) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — single-threaded flavor | ✅ | ✅ | ✅ | ~ | ✅ | ~ | ✅ | ✅ | — |
| Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle) | — | — | — | — | — | — | — | — | — |
| Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — async flavor (reader kinds + eventual transparency) | — | — | — | — | — | — | — | — | — |
| Broadcast topic (`TopicCell`) **Core surface** — single-threaded flavor — independent cursors + durable replay + safe GC (`#lztopiccell`) | ✅ | ✅ | ✅ | ~ | ✅ | ~ | ✅ | ✅ | — |
| Broadcast topic (`TopicCell`) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle) | — | — | — | — | — | — | — | — | — |
| Broadcast topic (`TopicCell`) **Core surface** — async flavor (reader kinds + eventual transparency) | — | — | — | — | — | — | — | — | — |
| Competing-consumer work queue (`WorkQueueCell`) **Core surface** — single-threaded flavor — exclusive leases + ack/nack + redelivery + DLQ (`#lzworkqueue`) | ✅ | ✅ | ✅ | ~ | ✅ | ~ | ✅ | ✅ | — |
| Competing-consumer work queue (`WorkQueueCell`) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle) | — | — | — | — | — | — | — | — | — |
| Competing-consumer work queue (`WorkQueueCell`) **Core surface** — async flavor (reader kinds + eventual transparency) | — | — | — | — | — | — | — | — | — |
| Merge algebra + `Source<T, M>` — associative `MergePolicy` (`KeepLatest`/`Sum`/`Max`/`SetUnion`/`RawFifo`), `Cell ≡ Source<KeepLatest>`, read-any-cell/write-`Source` split (`#relaycell`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| RelayCell — conflating relay + `BackpressurePolicy` + `SpillStore` + `Transport` + Inbox/Outbox + Rate/Window/Expiry/Priority/keyed policies (`#relaycell`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Free-text character CRDT (`TextCrdt`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `TextCrdt` delta sync (`version_vector` / `delta_since` / `apply_delta`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `CrdtTree` lossless document contract (`#lzcrdttree`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Move-aware sequence CRDT (`SeqCrdt`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree CRDT core (`LosslessTreeCrdt`, M1) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree — dotted-frontier anti-entropy | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree — concurrent merge convergence | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Registers (LWW / MV) + `PnCounter` + `CellCrdt` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| IPC wire — `Snapshot` + `Delta` + `CrdtSync` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Shared-memory blob path (`ShmBlobArena`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Cross-process zero-copy transport (`BlobBackend` / shm / arrow) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Distributed CRDT plane (`CrdtPlaneRuntime` / anti-entropy) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reliable sync — resync coordinator + at-least-once durable outbox + OR-set/LWW liveness (`#lzsync`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Storage-independent durable outbox (`OutboxStore` + shared outbox protocol; SQLite/Room/IndexedDB/file adapters) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reliable-sync transport seam + full-duplex `SyncDriver` loop (`IpcSink`/`IpcSource`, `#sync-driver`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Distributed plane — WebRTC transport + signaling | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| State projection / mirror | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Causal receipts (`CausalReceipts` outcome projection) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Message-passing + RPC command plane (`command-plane-v1`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| C-ABI FFI boundary | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Permission boundary (`PeerPermissions` / `RemoteOp`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Capability negotiation (`SessionHandshake`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Instrumentation / benchmarks | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Temporal sources — `TimerCell` / `IntervalCell` / `CronCell` / `DeadlineCell` over a logical clock (`#lztime`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Rate-shaping operators — `DebounceCell` / `ThrottleCell` / `SampleCell` / `ProbabilisticSampleCell` (`#lzrateshape`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Membership + failure detection — `MembershipCell` (SWIM + Phi-accrual) / `PeerSet` / `PeerChangeEvent` (`#lzmemb`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Distributed coordination — `LeaseCell` / `LeaderCell` / `LockCell` / `SemaphoreCell` / `BarrierCell`+`QuorumCell` (`#lzcoord`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Presence + ephemeral plane — `PresenceCell` / `AwarenessCell` / `EphemeralCell` + `Ephemeral`/`Durable` markers (`#lzpresence`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Stream windowing — `TumblingWindow` / `SlidingWindow` / `SessionWindow` over the merge algebra (`#lzwindow`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Fault tolerance — `CircuitBreakerCell` / `RetryPolicyCell` / `BulkheadCell` / `TimeoutCell` (`#lzresilience`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| Embedded-service plane — `HealthCell` / `ReadinessCell` / `DiscoveryCell` / `ServiceRegistry` (`#lzservice`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — |
<!-- coverage-table:end -->

## Development

```bash
make check          # build + test — run before committing
make conformance    # replay the shared lazily-spec fixtures only
make format-check   # dotnet format --verify-no-changes
```

## License

MIT
