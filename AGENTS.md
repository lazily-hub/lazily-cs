# lazily-cs

C#/.NET binding of the lazily reactive-signals family. The reactive kernel is a port of the
reference semantics, not an independent design: when this repo and
[lazily-spec](https://github.com/lazily-hub/lazily-spec) disagree, the spec wins and this repo is
the finding.

## Commit & Push

Commit and push completed work at the end of every turn that changed code, tests, docs, or the
coverage matrix — do not leave finished work uncommitted. Run `make check` first and ensure it is
green; stage only the files that belong to the change (never secrets or private customer names);
write a concise commit message in the repo's existing style; push to the current branch on
`origin`. This standing rule overrides the harness default of "commit only when explicitly asked"
for this repo.

## Layout

```
src/Lazily/            the library (net10.0)
tests/Lazily.Tests/    xunit suite + the spec conformance runners
Lazily.sln
```

## Verification

```bash
make check          # build + test — the local gate
make conformance    # replay the shared lazily-spec fixtures only
make format-check   # dotnet format --verify-no-changes
```

The conformance corpus resolves through the sibling-relative path
`../lazily-spec/conformance`. Clone lazily-spec beside this repo before running the suite.

## Conformance discipline

These rules are not style preferences; each one exists because its absence produced a suite that
reported green while testing nothing.

- **Never vendor fixtures.** A bundled copy drifts from the spec. One sibling-relative path, and
  CI clones the corpus explicitly.
- **Absence is a failure, not a skip.** The runner asserts the corpus resolved, that a positive
  number of fixtures replayed, and that a floor of assertions actually ran. A skip-if-absent
  runner with no guard is worse than no runner at all.
- **Ledgers are two-directional.** `Unsupported` names fixtures this binding cannot execute *and
  the exact op or assertion that blocks it*; `KnownDivergences` names assertions it does not
  satisfy. Both are asserted to match the observed set EXACTLY — a new entry fails the build, and
  a fixed one fails it until the entry is deleted.
- **A ledger entry is a finding against this binding, never a relaxation of a fixture.** If a
  fixture looks wrong, take it up in lazily-spec; do not weaken it here.
- **Assert the library, not the runner's bookkeeping.** Counters that pin library behaviour live
  inside the library's own call path — the merge-fold counter is installed *in the merge policy*,
  so `merges_of` counts folds the library performed rather than calls the runner issued. A runner
  that counts its own intentions cannot detect a binding that dropped the work.
- **Replay every execution model, not just the easy one.** The reactive-graph runner is
  parameterised over `IGraphModel` and runs the same op stream against `Context`,
  `ThreadSafeContext`, and `AsyncContext`. A cascade that stops one level below the write is
  *correct* synchronously and *broken* asynchronously — a slot read short-circuits on a resolved
  cache, so the stale value is served forever and no pull chain rescues it. No synchronous replay
  of any fixture can see that.
- **Gate a missing construct; never degrade it.** When one plane does not ship a construct a
  fixture needs, the fixture/model pair goes in the per-model ledger with the reason. Substituting
  the nearest available thing is the worst option: a lazy slot standing in for an eager signal
  produces plausible numbers that satisfy two of the three assertions a signal fixture makes.

## Divergences from the reference bindings

Recorded here so they are decisions rather than drift:

- **No `comparable` bound on a source's value.** The write guard uses
  `EqualityComparer<T>.Default`, with an explicit `IEqualityComparer<T>` overload on every
  constructor. Strictly more general than the Rust/Go `==` bound.
- **Node constructors are extension methods** on `Context` (in `Reactive`), so the factory names
  can match the family vocabulary without colliding with the type names they return.
- **The compute-view fortification guard is a runtime check.** C# cannot bind the view to its
  recompute the way a Rust lifetime does, so an escaped view throws `StaleComputeException`.
- **`AsyncContext` serializes on a lock, not on an owner loop.** lazily-go funnels every graph
  mutation through a single goroutine and a command channel. .NET's `Monitor` is reentrant, so the
  same invariant — graph state touched by one thread at a time — is expressed directly as one
  lock. Bodies run off the lock on the thread pool; their completions re-enter it.
- **The async invalidation walk is non-consuming.** lazily-go's async walk terminates by deleting
  the edges it traverses; this binding uses a visited set and leaves the edges in place, matching
  its own synchronous kernel. Degree assertions therefore mean the same thing on all three
  execution models.
- **An async slot in `Error` retries on the next read.** lazily-go serves the stored error
  forever. `docs/async.md` lists `Error → Computing` — "`get_async` retry after an error" — and the
  spec is the authority, so a transient failure is not made permanent here.

## Not yet implemented

Everything in the feature matrix except the reactive graph, the merge algebra, and the
concurrency layers (thread-safe and async contexts). Do not mark a row shipped in lazily-spec's
`coverage.json` until its conformance corpus replays here with an empty ledger.

The remaining phases and the corpus each must replay are tracked in
`tasks/software/lazily-cs-parity-plan.md` in the operator workspace.
