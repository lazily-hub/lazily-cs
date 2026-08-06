# R3 adapter

`Lazily.R3` is an optional net10.0 package. The core `Lazily` assembly has no R3 dependency.

## Lazily to R3

`Context.ToR3State` is cold: each subscription owns one `Effect`, emits the current value, tracks
dynamic dependencies, suppresses equal values, and detaches its edges when disposed.
`ToSharedR3State` is explicit sharing: one owned effect feeds a one-value `ReplaySubject`.

An R3 error is recoverable (`OnErrorResume`) and does not tear down the effect. Disposing a shared
bridge completes its current subscribers. Lazily batching may collapse intermediate state values,
so this is a state projection, not a lossless event-stream adapter.

## R3 to Lazily

`Context.BindR3State` requires same-thread ingress. `ThreadSafeContext.BindR3State` serializes
cross-thread ingress through the context lock. Both return an equality-guarded `Source<T>` and an
owned subscription. R3 errors update `LastError` and remain recoverable; R3 completion is terminal
and recorded in `Completion`. Disposal unsubscribes but leaves the last state readable. Reentrant
writes follow the underlying Context/ThreadSafeContext semantics, and `Batch` coalescing remains a
Lazily concern.

## Performance

`benchmarks/Lazily.R3.Benchmarks` permanently compares native Lazily, native R3, and both bridge
directions under BenchmarkDotNet's memory diagnoser. Profiling before budgets were introduced
observed 488 B/update. A later short run on .NET 10.0.10 observed 336 B/update for native Lazily and
both bridge directions (native R3 allocated 0 B/update). These are environment-specific profiling
observations, not budgets. Set budgets only after repeated runs on stable hardware and allocation
source profiling.
