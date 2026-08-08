# lazily-cs

Lazy reactive primitives for C#/.NET — `Source`, `Computed`, and `Effect` with automatic
dependency tracking, plus the [lazily-spec](https://github.com/lazily-hub/lazily-spec) wire
protocol, CRDTs, and distributed plane.

This is the ninth binding in the lazily family. The reactive kernel is a direct port of the
reference semantics, and it replays the shared cross-language conformance corpus rather than
asserting behaviour it invented locally.

The `Lazily.InteropPeer` console project is the production-backed adapter for the
cross-binding network suite. It advertises `distributed_crdt` with the JSON
codec, routes semantic operations through `CrdtPlaneRuntime` and `IpcWire`, and
leaves transport links unadvertised until executable channel adapters exist.

> **Status: active and spec-conformant.** The binding replays all 124 canonical fixtures.
> Feature-specific peers remain staged until that execution flavor exists; the generated matrix
> below is the honest, single-source record of what can join each peer group.

## Install

```bash
dotnet add package Lazily
```

Targets `netstandard2.1`, `net8.0`, and `net10.0`.

The optional net10-only R3 bridge is packaged separately:

```bash
dotnet add package Lazily.R3
```

See [R3 adapter semantics](docs/r3-adapter.md) for ownership, threading, errors, completion,
batching, and the state-not-events boundary.

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

## Distributed and native planes

`StateProjection` atomically folds canonical `SnapshotMessage` and `DeltaMessage` frames into a
receiver-side graph mirror; gaps and invalid batches fail closed without partial state. Its
producer-side counterpart, `StateProjectionMirror`, emits sorted, coalesced deltas. Queue deltas
remain a separate collection projection and are rejected by the graph mirror instead of being
silently accepted.

The `command-plane-v1` surface is the typed `CommandMessage` family plus `CommandWire`,
`CommandProjection`, and `CommandRpcClient`. Terminal causal receipts are authoritative, stale
generations are ignored, conflicting terminals fail closed, and `NegotiatedSession` prevents RPC
use unless both `SessionHandshake` peers advertised the feature. `PeerPermissions` independently
gates remote reads, writes, effects, subscriptions, snapshots, deltas, and CRDT operations with a
default-deny policy.

For native consumers, `include/lazily_ffi.h` exposes the normative C ABI over NativeAOT. The smoke
gate publishes the shared library, verifies all exported symbols, links a C11 consumer, and runs
it against the real IPC codec:

```bash
./scripts/check-ffi.sh
```

`LazilyMetrics.Snapshot()` reports production-path counters without coupling callers to a metrics
backend, while `LazilyBenchmark.RunSuite()` provides deterministic in-process benchmark probes.

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
until its entry is deleted. Today lazily-cs opens and replays **all 124 canonical fixture files**
with an empty fixture ledger. Support remains feature-specific: for example, the synchronous queue,
topic, and work-queue peers participate, while their not-yet-implemented thread-safe and async
flavors remain staged in the matrix.

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
#### Summary — family × language

| Family | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reactive graph | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Materialization | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Family sync | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Statecharts | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Keyed collections | ✅ | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ |
| Reactive queue | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Broadcast topic | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Work queue | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| CRDT data types | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Egress | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Ingress | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Wire codec | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ | ✅ |
| Transport & FFI | ✅ | ✅ | ✅ | ~ | ~ | ✅ | ✅ | ~ | ✅ |
| Message passing | ✅ | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ |
| Reliable sync | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ |
| Distributed plane | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Causal receipts | ~ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Security boundary | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Membership | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Coordination | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Presence | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Temporal | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Rate shaping | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Windowing | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Resilience | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Portable stdlib | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Service plane | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Instrumentation | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

**Roll-up rule:** a family cell is `✅` only when *every required* row in that family is `✅`; `~` when the family is mixed (some shipped or partial); `—` when no required row is shipped or partial; `⊘` only when every required row in the family is not applicable. Rows the spec marks **MAY** (`optional`, shown as *opt* below) are excluded from the roll-up — declining an optional feature is not a gap.

#### Reactive graph

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reactive graph [^reactive-graph] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Thread-safe context [^thread-safe-context] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Async reactive context [^async-reactive-context] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Merge algebra [^merge-algebra] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Materialization

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Keyed-map materialization [^keyed-map-materialization] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Thread-safe keyed map [^thread-safe-keyed-map] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Async keyed map [^async-keyed-map] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Family sync

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Keyed-map sync [^keyed-map-sync] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Statecharts

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Flat state machine [^flat-state-machine] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Harel state charts [^harel-state-charts] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Keyed collections

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Keyed reactive maps [^keyed-reactive-maps] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| ReactiveMap core — single-threaded [^reactivemap-core-single-threaded] | ✅ | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ |
| ReactiveMap core — thread-safe [^reactivemap-core-thread-safe] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| ReactiveMap core — async [^reactivemap-core-async] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Exact-key dependency availability [^exact-key-dependency-availability] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Atomic ordered move [^atomic-ordered-move] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Memoized semantic tree [^memoized-semantic-tree] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Stable-id alignment [^stable-id-alignment] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Reactive queue

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reactive queue core — single-threaded [^reactive-queue-core-single-threaded] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reactive queue core — thread-safe [^reactive-queue-core-thread-safe] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reactive queue core — async [^reactive-queue-core-async] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Broadcast topic

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Broadcast topic core — single-threaded [^broadcast-topic-core-single-threaded] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Broadcast topic core — thread-safe [^broadcast-topic-core-thread-safe] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Broadcast topic core — async [^broadcast-topic-core-async] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Work queue

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Work queue core — single-threaded [^work-queue-core-single-threaded] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Work queue core — thread-safe [^work-queue-core-thread-safe] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Work queue core — async [^work-queue-core-async] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### CRDT data types

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Free-text character CRDT [^free-text-character-crdt] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| TextCrdt delta sync [^textcrdt-delta-sync] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| CrdtTree lossless document [^crdttree-lossless-document] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Move-aware sequence CRDT [^move-aware-sequence-crdt] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Registers (LWW/MV) + PnCounter [^registers] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Lossless tree

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Lossless tree CRDT core [^lossless-tree-crdt-core] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree — anti-entropy [^lossless-tree-anti-entropy] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Lossless tree — merge convergence [^lossless-tree-merge-convergence] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Egress

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| RelayCell [^relaycell] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Ingress

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reactive ingress [^reactive-ingress] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Ingress — thread-safe [^ingress-thread-safe] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Ingress — async [^ingress-async] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Wire codec

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| IPC wire — Snapshot/Delta/CrdtSync [^ipc-wire] | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ | ✅ |
| Frame codec — json [^frame-codec-json] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Frame codec — msgpack [^frame-codec-msgpack] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Frame codec — postcard *(opt)* [^frame-codec-postcard] | ✅ | — | — | — | — | — | — | — | — |
| NodeId/PeerId exact-representation [^nodeid-peerid-exact-representation] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| NodeKey null-leniency [^nodekey-null-leniency] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Capability negotiation [^capability-negotiation] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Transport & FFI

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Shared-memory blob path [^shared-memory-blob-path] | ✅ | ✅ | ✅ | ~ | ~ | ✅ | ✅ | ~ | ✅ |
| Cross-process zero-copy transport [^cross-process-zero-copy-transport] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| C-ABI FFI boundary [^c-abi-ffi-boundary] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Message passing

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Message-passing + RPC command plane [^message-passing-rpc-command-plane] | ✅ | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | ✅ | ✅ |

#### Reliable sync

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Reliable sync [^reliable-sync] | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ |
| Storage-independent durable outbox [^storage-independent-durable-outbox] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Reliable-sync transport seam [^reliable-sync-transport-seam] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Distributed plane

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Distributed CRDT plane [^distributed-crdt-plane] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Distributed plane — WebRTC [^distributed-plane-webrtc] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| State projection / mirror [^state-projection-mirror] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Causal receipts

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Causal receipts [^causal-receipts] | ~ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Security boundary

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Permission boundary [^permission-boundary] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Membership

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Membership + failure detection [^membership-failure-detection] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Coordination

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Distributed coordination [^distributed-coordination] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Presence

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Presence + ephemeral plane [^presence-ephemeral-plane] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Temporal

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Temporal sources [^temporal-sources] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Rate shaping

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Rate-shaping operators [^rate-shaping-operators] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Windowing

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Stream windowing [^stream-windowing] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Resilience

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Fault tolerance [^fault-tolerance] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Portable stdlib

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Portable stdlib Timer [^portable-stdlib-timer] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Portable stdlib Timeout [^portable-stdlib-timeout] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Portable stdlib RevisionBarrier [^portable-stdlib-revision-barrier] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Service plane

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Embedded-service plane [^embedded-service-plane] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

#### Instrumentation

| Feature | Rust | Python | Kotlin | JS | Dart | Zig | Go | C++ | C# |
| --------- | :----: | :------: | :------: | :--: | :----: | :---: | :--: | :---: | :--: |
| Instrumentation / benchmarks [^instrumentation-benchmarks] | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

[^reactive-graph]: Reactive graph — two cell kinds (nodes `SourceCell` / `ComputedCell`; handles `Source<T, M>` / `Computed<T>`) + `Effect` sink + eager `Computed` (`computed().eager()`) / all cells guarded / batch
[^keyed-map-materialization]: Keyed-map materialization (`ComputedMap`) — mint-on-access derived slots: transparency + deferral (`#lzmatmode`)
[^thread-safe-keyed-map]: Thread-safe keyed map (`ThreadSafeComputedMap`) — `Send + Sync` + materialization confluence (`#lzmatmode`)
[^async-keyed-map]: Async keyed map (`AsyncComputedMap`) — eventual transparency (`#lzmatmode`)
[^keyed-map-sync]: Keyed-map sync — membership propagation + materialize-on-ingest + derived-aggregate transparency (`#lzfamilysync`)
[^thread-safe-context]: Thread-safe context (lock-backed)
[^async-reactive-context]: Async reactive context
[^flat-state-machine]: Flat state machine
[^harel-state-charts]: Harel state charts
[^keyed-reactive-maps]: Keyed reactive maps (`ReactiveMap`: `SourceMap` / `ComputedMap`) + `SourceTree` + reconcile
[^reactivemap-core-single-threaded]: `ReactiveMap` **Core surface** — single-threaded flavor (cell-model.md § Core surface vs. binding extensions)
[^reactivemap-core-thread-safe]: `ReactiveMap` **Core surface** — thread-safe flavor (ordering + membership reactivity)
[^reactivemap-core-async]: `ReactiveMap` **Core surface** — async flavor (ordering + membership reactivity)
[^exact-key-dependency-availability]: Exact-key dependency availability (`DependencyMap`: observe before publish, unrelated-key isolation, stable identity; `#lzdependencyavailability`)
[^atomic-ordered-move]: Atomic ordered move replayed against **all three flavors** (`cellmap_atomic_move` + `cellmap_independence`)
[^memoized-semantic-tree]: Memoized semantic tree (`SemTree`)
[^stable-id-alignment]: Stable-id alignment (manufactured identity)
[^reactive-queue-core-single-threaded]: Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — single-threaded flavor
[^reactive-queue-core-thread-safe]: Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle)
[^reactive-queue-core-async]: Reactive queue (`QueueCell` SPSC/MPSC + `QueueStorage` adapter) **Core surface** — async flavor (reader kinds + eventual transparency)
[^broadcast-topic-core-single-threaded]: Broadcast topic (`TopicCell`) **Core surface** — single-threaded flavor — independent cursors + durable replay + safe GC (`#lztopiccell`)
[^broadcast-topic-core-thread-safe]: Broadcast topic (`TopicCell`) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle)
[^broadcast-topic-core-async]: Broadcast topic (`TopicCell`) **Core surface** — async flavor (reader kinds + eventual transparency)
[^work-queue-core-single-threaded]: Competing-consumer work queue (`WorkQueueCell`) **Core surface** — single-threaded flavor — exclusive leases + ack/nack + redelivery + DLQ (`#lzworkqueue`)
[^work-queue-core-thread-safe]: Competing-consumer work queue (`WorkQueueCell`) **Core surface** — thread-safe flavor (reader kinds + closure lifecycle)
[^work-queue-core-async]: Competing-consumer work queue (`WorkQueueCell`) **Core surface** — async flavor (reader kinds + eventual transparency)
[^merge-algebra]: Merge algebra + `Source<T, M>` — associative `MergePolicy` (`KeepLatest`/`Sum`/`Max`/`SetUnion`/`RawFifo`), `Cell ≡ Source<KeepLatest>`, read-any-cell/write-`Source` split (`#relaycell`)
[^relaycell]: RelayCell — conflating relay + `BackpressurePolicy` + `SpillStore` + `Transport` + Inbox/Outbox + Rate/Window/Expiry/Priority/keyed policies (`#relaycell`)
[^free-text-character-crdt]: Free-text character CRDT (`TextCrdt`)
[^textcrdt-delta-sync]: `TextCrdt` delta sync (`version_vector` / `delta_since` / `apply_delta`)
[^crdttree-lossless-document]: `CrdtTree` lossless document contract (`#lzcrdttree`)
[^move-aware-sequence-crdt]: Move-aware sequence CRDT (`SeqCrdt`)
[^lossless-tree-crdt-core]: Lossless tree CRDT core (`LosslessTreeCrdt`, M1)
[^lossless-tree-anti-entropy]: Lossless tree — dotted-frontier anti-entropy
[^lossless-tree-merge-convergence]: Lossless tree — concurrent merge convergence
[^registers]: Registers (LWW / MV) + `PnCounter` + `CellCrdt`
[^ipc-wire]: IPC wire — `Snapshot` + `Delta` + `CrdtSync`
[^frame-codec-json]: Frame codec — `json` **reference codec**: dependency-free interop floor, FFI baseline form, byte-canonical (**MUST**) — executable round-trip obligation (`conformance/codec/frame_roundtrip_json.json`, `#lzmsgpackparity`)
[^frame-codec-msgpack]: Frame codec — `msgpack` **cross-language binary default**: externally-tagged frame over named-field maps, semantic (not byte-identical) round-trip (**MUST**) — executable round-trip obligation (`conformance/codec/frame_roundtrip_msgpack.json`, `#lzmsgpackparity`). Shipping *a* MessagePack codec does not earn this mark: lazily-cpp read `~` here while its private internally-tagged framing wore the token, and only flipped once it shipped the spec wire (`#lzcppmsgpackwire`)
[^frame-codec-postcard]: Frame codec — `postcard` positional same-schema fast path: smallest + byte-canonical, not cross-language (**MAY**)
[^nodeid-peerid-exact-representation]: `NodeId` / `PeerId` exact-representation bound (**MUST**) — a decoder that cannot represent a received identifier exactly rejects the frame rather than rounding it (`conformance/codec/nodeid_exact_range.json`, `#lzspecdecoderbound`). A binding's exact range MAY be narrower than the `u64` wire type; ✅ means it refuses outside that range instead of substituting a neighbouring id, not that it carries the full `u64`. Exact ranges: full `u64` in Rust / Zig / C#, unbounded in Python, `[0, 2^63)` in Kotlin / Go / C++, `[0, 2^53)` in JS, and platform-split in Dart (63-bit on the VM, 53-bit on web). protocol.md stated only the PRODUCER half until this audit, and two C++ decoders were substituting rather than refusing.
[^nodekey-null-leniency]: `NodeKey` null-leniency on decode (**MUST**) — omit-when-absent binds the ENCODER; a decoder reads both an omitted `key` and an explicit `key: null` as absent, refusing neither and constructing a key from neither (`conformance/codec/nodekey_null_leniency.json`, `#lzkeynullstrict`). Replayed on BOTH optional-key sites (`NodeSnapshot`, the `NodeAdd` delta op) in both codecs, and the fixture pins the RE-ENCODED field set as well: reading null as absent and writing it back out is a correct decode with a non-conforming encoder. Before the audit lazily-py and lazily-zig refused the null form, and lazily-kt decoded it into a real key named `null` — all three had the same field right on `CrdtOp`, in the same file.
[^shared-memory-blob-path]: Shared-memory blob path (`ShmBlobArena`)
[^cross-process-zero-copy-transport]: Cross-process zero-copy transport (`BlobBackend` / shm / arrow)
[^distributed-crdt-plane]: Distributed CRDT plane (`CrdtPlaneRuntime` / anti-entropy)
[^reliable-sync]: Reliable sync — resync coordinator + at-least-once durable outbox + OR-set/LWW liveness (`#lzsync`)
[^storage-independent-durable-outbox]: Storage-independent durable outbox (`OutboxStore` + shared outbox protocol; SQLite/Room/IndexedDB/file adapters)
[^reliable-sync-transport-seam]: Reliable-sync transport seam + full-duplex `SyncDriver` loop (`IpcSink`/`IpcSource`, `#sync-driver`)
[^distributed-plane-webrtc]: Distributed plane — WebRTC transport + signaling
[^state-projection-mirror]: State projection / mirror
[^causal-receipts]: Causal receipts (`CausalReceipts` outcome projection)
[^message-passing-rpc-command-plane]: Message-passing + RPC command plane (`command-plane-v1`)
[^c-abi-ffi-boundary]: C-ABI FFI boundary
[^permission-boundary]: Permission boundary (`PeerPermissions` / `RemoteOp`)
[^capability-negotiation]: Capability negotiation (`SessionHandshake`)
[^instrumentation-benchmarks]: Instrumentation / benchmarks
[^temporal-sources]: Temporal sources — `TimerCell` / `IntervalCell` / `CronCell` / `DeadlineCell` over a logical clock (`#lztime`)
[^rate-shaping-operators]: Rate-shaping operators — `DebounceCell` / `ThrottleCell` / `SampleCell` / `ProbabilisticSampleCell` (`#lzrateshape`)
[^membership-failure-detection]: Membership + failure detection — `MembershipCell` (SWIM + Phi-accrual) / `PeerSet` / `PeerChangeEvent` (`#lzmemb`)
[^distributed-coordination]: Distributed coordination — `LeaseCell` / `LeaderCell` / `LockCell` / `SemaphoreCell` / `BarrierCell`+`QuorumCell` (`#lzcoord`)
[^presence-ephemeral-plane]: Presence + ephemeral plane — `PresenceCell` / `AwarenessCell` / `EphemeralCell` + `Ephemeral`/`Durable` markers (`#lzpresence`)
[^stream-windowing]: Stream windowing — `TumblingWindow` / `SlidingWindow` / `SessionWindow` over the merge algebra (`#lzwindow`)
[^fault-tolerance]: Fault tolerance — `CircuitBreakerCell` / `RetryPolicyCell` / `BulkheadCell` / `TimeoutCell` (`#lzresilience`)
[^portable-stdlib-timer]: Portable stdlib `Timer` (`stdlib_timer_v1`) — canonical fixture + mutation-gate verified
[^portable-stdlib-timeout]: Portable stdlib caller-driven `Timeout<T>` (`stdlib_timeout_v1`) — distinct from reactive `TimeoutCell`
[^portable-stdlib-revision-barrier]: Portable stdlib `RevisionBarrier` (`stdlib_revision_barrier_v1`) — register/recheck lost-wakeup guard
[^embedded-service-plane]: Embedded-service plane — `HealthCell` / `ReadinessCell` / `DiscoveryCell` / `ServiceRegistry` (`#lzservice`)
[^reactive-ingress]: Transport-agnostic reactive ingress (`IngressCell`) — keyed lifecycle scopes, generation/sequence/freshness envelopes, reorder buffer, accepted/dropped/error receipt readers (`#designimplementtransport`)
[^ingress-thread-safe]: Ingress family — `Send + Sync` flavor (`ThreadSafeIngressCell`): one frontier walk per admission (`#designimplementtransport`)
[^ingress-async]: Ingress family — async flavor (`AsyncIngressCell`): admission is not async-coloured (`#designimplementtransport`)
<!-- coverage-table:end -->

## Development

```bash
make check          # build + test — run before committing
make conformance    # replay the shared lazily-spec fixtures only
make format-check   # dotnet format --verify-no-changes
```

## The lazily family

lazily is one reactive kernel — `Source` / `Computed` / `Effect`, keyed
collections, state charts, CRDTs, and a distributed plane — implemented natively
in each language and held to a single cross-language contract:

- [`lazily-spec`](https://github.com/lazily-hub/lazily-spec) — the wire protocol,
  the generated feature matrix, and the conformance corpus every binding replays.
- [`lazily-formal`](https://github.com/lazily-hub/lazily-formal) — the Lean 4
  formal model the bindings share.

| repo | language |
|---|---|
| [`lazily-rs`](https://github.com/lazily-hub/lazily-rs) | Rust — the reference implementation |
| [`lazily-py`](https://github.com/lazily-hub/lazily-py) | Python |
| [`lazily-go`](https://github.com/lazily-hub/lazily-go) | Go |
| [`lazily-kt`](https://github.com/lazily-hub/lazily-kt) | Kotlin / JVM |
| [`lazily-js`](https://github.com/lazily-hub/lazily-js) | JavaScript / TypeScript |
| **`lazily-cs`** | C# / .NET — you are here |
| [`lazily-cpp`](https://github.com/lazily-hub/lazily-cpp) | C++ |
| [`lazily-zig`](https://github.com/lazily-hub/lazily-zig) | Zig |
| [`lazily-dart`](https://github.com/lazily-hub/lazily-dart) | Dart / Flutter |
| [`lazily-react`](https://github.com/lazily-hub/lazily-react) | React / Preact bindings layered over [`lazily-js`](https://github.com/lazily-hub/lazily-js) — not a separate language binding |

The per-binding parity matrix above is generated from `coverage.json` in
[`lazily-spec`](https://github.com/lazily-hub/lazily-spec), which stays the
single source for cross-binding feature coverage.

## License

MIT
