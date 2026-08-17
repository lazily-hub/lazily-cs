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
- **Every rung carries a positive-evidence floor.** Each rung of
  `scripts/check-conformance-coverage.sh` reasons about a population — canonical fixtures listed,
  fixtures opened, scenarios of opened fixtures — and *every one of them is vacuously satisfied by
  an empty population*: zero fixtures cannot produce an uncovered fixture, zero scenarios cannot
  produce an unreplayed scenario. A loop that finds no problems cannot tell "nothing is wrong"
  from "nothing was examined", so the magnitude is asserted explicitly before anything prints OK
  (`#lzvacuousrun`). Rung 1 fails on a corpus listing zero fixtures and on `covered <
  MIN_FIXTURES` (145); rung 4 fails on zero scenarios across the opened fixtures and on
  `replayed < MIN_SCENARIOS` (165); the assertion-block rung carries `MIN_BLOCKS` (33) on the
  same terms. Each floor is the EXACT count a completed CI run reports against the published
  corpus. This paragraph used to describe the scenario floor as "calibrated below the observed",
  and that advice was the bug: a floor deliberately set under reality has stopped guarding,
  because the drop it exists to catch fits inside its own margin. It let 17 scenarios of slack
  accumulate here before the family-wide repin (`#lzscenariofloordrift`). Set a floor to the
  number the gate itself prints, never to old-value-plus-your-delta, and prove it by setting it
  to N+1 and watching the guard fail. Both floors are env-overridable so they can be
  mutation-checked, and neither is ever lowered to make a red run green — a drop is the finding.
  The scenario guard also refuses to run at all if `MIN_SCENARIOS`
  is not passed through to it: a floor that cannot be read is not a floor, and "cannot check"
  must never share a branch with "nothing to check".
- **The evidence channel guards itself.** A floor asserts the manifest is big enough; it cannot
  say the manifest describes THIS corpus. Every bare id recorded in the manifest is resolved
  against `$SPEC_DIR`, and one that names no file there fails the build: the recorder truncated or
  interleaved its writes (several test hosts append at process exit), or the evidence file is
  left over from a run against a different corpus. Either way the count is inflated by ids nobody
  can resolve, and a fixture the suite stopped opening can hide behind them. The scenario leg makes
  the same check one rung down — an id the fixture does not carry means the runner and the corpus
  disagree about how a scenario is named.
- **Ledgers are two-directional.** `Unsupported` names fixtures this binding cannot execute *and
  the exact op or assertion that blocks it*; `KnownDivergences` names assertions it does not
  satisfy. Both are asserted to match the observed set EXACTLY — a new entry fails the build, and
  a fixed one fails it until the entry is deleted.
- **A ledger entry is a finding against this binding, never a relaxation of a fixture.** If a
  fixture looks wrong, take it up in lazily-spec; do not weaken it here.
- **A paragraph is DISCHARGED, never asserted and never excused** (`#lzprosekeyconvention`). The
  CORPUS says which keys of an `assertions` block are English paragraphs, in `assertions.prose`;
  a binding never decides for itself. Discharge each one with `meta.ProseKey("clause",
  "backends", "scenario_count")`, naming the executable keys that carry its obligation, and close
  the fixture with `prose.VerifyProse(fixture)` inside `ProseLedger.Replay(...)`. The ledger is
  FIXTURE-scoped, not block-scoped: `epoch_disambiguation` is discharged by `expect.frame_epoch`
  and `expect.blob_epoch`, asserted per scenario long after the `assertions` block is finished, so
  every block of that fixture takes the same ledger. Asserting a paragraph pins wording rather
  than behaviour — a copy-edit reddens the run and a library regression does not — and excusing
  one with free text ("prose: it explains why…") is unfalsifiable, which is what nine bindings
  each defaulted to differently. Both fail, as does a discharge naming nothing, naming a key this
  fixture's run never asserted, naming another paragraph, or naming `prose` itself — the last is
  not redundant, since `prose` never self-lists and rule 4's own comparison marks it asserted, so
  the prose-name set is SEEDED with it.
  - **The corpus declaration is read off the RAW block, before any name-based exemption.**
    `note` / `description` / `reason` stay exempt BY NAME in per-step and per-scenario blocks, but
    a tracker that subtracts its reserved names first makes a declaration invisible: the key is
    exempt from the unread guard, exempt from the unasserted guard, and never discharged, so both
    `frame_roundtrip_*` fixtures would skip the convention entirely while this binding still
    reported conforming. Three of nine hit that independently. Inside a declaring block the name
    exemption is off ENTIRELY — a `note` sitting there but absent from the array needs an
    assertion or an excuse like any other key.
  - **A run is ONE TEST.** The ledger belongs to a `ProseLedger.Replay` scope and is cleared at
    its verification; unioning asserted keys across tests would let a discharge in one test be
    satisfied by an assertion in another. The net is armed by that scope, never registered by the
    first `ProseKey` — every teardown mechanism runs LIFO, so a net armed by the first discharge
    fires before a verification registered earlier and reports a false failure.
  - **Rule 8 lives in `scripts/check-conformance-coverage.sh`, not in the test host.** Rules 1-7
    are all satisfied over an EMPTY population, so a declaring fixture that is opened and never
    replayed passes every one of them while proving nothing — the vacuity `anti_vacuity` exists to
    name, reappearing in the guard meant to enforce it. `VerifyProse` records evidence in the
    runtime manifest and the guard derives the REQUIRED set from the corpus (`assertions.prose` on
    disk), never from a count kept here. Two-directional, like every other ledger in this repo.
  - **A discharge is checked for truth, not for sufficiency, so what it NAMES matters.** Rule 6
    only proves the named key was asserted. A key that compares the fixture to itself
    (`scenario_count` against the fixture's own `scenarios` array) or against a hand-written
    literal (`codecs`, `key_forms`) is asserted, satisfies rule 6, and discharges nothing — it is
    green over a runner that decodes nothing. Compare such keys against what the run really
    replayed BEFORE naming them. Where a paragraph is carried only by PROXY — `wire_encoding` is
    an obligation on the runner that no assertion key reddens, `theorem` names a Lean proof in
    lazily-formal — naming the closest executable key is conforming, and the call site says so,
    because that judgement is the reviewer's and the call site is where review sees it.
- **Opening a fixture is not replaying every scenario in it.** A fixture carrying several named
  scenarios can be PARTIALLY replayed while the coverage guard stays green — it asks only whether
  the FILE was opened, and one scenario answers yes; the key trackers only bind blocks a runner
  reaches, so a scenario nobody reached contributes no unconsumed and no unasserted key. Reach
  every scenario through `SpecCorpus.Scenarios(...)`, which hands back a `Scenario` booked on the
  first read of its PAYLOAD — never at the yield (`#lzscenariobodyskip`). Yielding is not
  replaying: an iterator cannot tell a loop body that ran from one that `continue`d, so the
  yield-time booking this replaced credited exactly the skip this rung exists to catch, which
  lazily-py demonstrated against the contract's own probe. `id`/`name`/`description` and the rest
  of `ScenarioSet.LabelKeys` stay silent, so a dispatch chain that reads the label and matches no
  arm books nothing; `.Value` (and the implicit `JsonElement` conversion) books, because handing
  the whole object to a replay helper is the strongest statement that a replay is happening, and
  `.Peek` is the escape hatch that does not. `scripts/check-conformance-coverage.sh` verifies
  that ledger against the ids on disk (`id`, else `name`, else positional `#<n>`), and
  `KNOWN_UNREPLAYED_SCENARIOS` sits beside `KNOWN_UNCOVERED` so there is one place to read what
  this binding does not prove. Naming a scenario is not replaying it: `IdAt` deliberately records
  nothing, and neither does a bookkeeping read of the raw array. Give every scenario dispatch
  chain a fail-closed `default`/`else` so an unmatched shape throws instead of passing.
- **A guard CI does not run is not a guard.** The four conformance rungs do not all execute the
  same way, and CI enforced only half of them until `#lzguardsnotinci`. The unconsumed-key and
  read-but-not-asserted gates are raised by `FixtureAssertions.Verify()` *inside the test host*, so
  any `dotnet test` step enforces them. The fixture-coverage guard and the scenario replay ledger
  live in `scripts/check-conformance-coverage.sh`, which only `make check` invoked — a CI that runs
  `dotnet test` alone stays green while skipping them silently. CI now invokes the script
  explicitly, with `LAZILY_CONFORMANCE_MANIFEST` set to an ABSOLUTE path and truncated first
  (exactly what the Makefile's `test` target does), and asserts positive fixture and scenario
  counts from the outside. The script's "corpus absent, skip" branch is split by CONTEXT rather
  than by opt-in: a non-empty `CI` makes an absent corpus a hard failure, because there it is
  missing EVIDENCE (the checkout is wrong) rather than evidence of absence. `LAZILY_CONFORMANCE_STRICT=1`
  remains as the explicit override for an environment that asserts presence without setting `CI`,
  but it is no longer the only thing standing between a wrong checkout and a green run — a flag
  only this workflow remembers to set is not a guard, since a new job, a reusable workflow, or a
  fork that forgets it gets the laptop behaviour. Evidence a run never wrote is not evidence of
  absence, and a guard reading an evidence file from an earlier run is not measuring this one.
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

- **No deprecated aliases for the pre-v2 collection names.** The other eight bindings keep
  `CellMap` / `SlotMap` (and their ThreadSafe/Async variants) and `CellTree` as deprecated aliases
  of `SourceMap` / `ComputedMap` and `SourceTree`. C# has no exportable generic type alias —
  `using X<T> = Y<T>` is file-scoped — so the only shim available is an `[Obsolete]` subclass,
  which forces the renamed types to be unsealed and is still one-directional (a `SourceMap` the
  library returns is not a `CellMap`). Deprecation exists to protect existing callers; this
  binding has none, since it is unpublished and deliberately excluded from the release train.
  Permanently weakening a public type to serve zero consumers is the worse trade, so the old
  names are simply gone here.

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

- **The async ingress flavor's READS are Task-typed; its ops are not.** lazily-rs's
  `AsyncIngressCell` uses a synchronous compute on the async graph and returns plain values, because
  admission awaits nothing. That half holds here: every `AsyncIngressCell` op
  (`Admit`/`Drain`/`Suspend`/`Reconnect`/`Close`/`Fail`/`Tick`) is synchronous and every reader body
  resolves with `Task.FromResult`. What cannot follow is the READ — this binding's `AsyncContext`
  takes `Func<AsyncCompute, Task<T>>` and `AsyncComputed<T>.GetAsync()` returns a `Task<T>` — so
  `ValueAsync`/`ReadinessAsync`/`AuthorityAsync`/`RetryAsync` are Task-typed. That is a property of
  the async graph, not of the ingress algebra, and the conformance runner bridges it exactly as
  `AsyncGraphModel` does rather than pretending the plane is synchronous.

- **The async queue-family flavor's READS are Task-typed; its ops are not.** Same split, same
  reason as the ingress flavor above: what a push, an advance, or a reap changed is a function of
  state the graph does not own, so every `AsyncQueueCell` / `AsyncTopicCell` /
  `AsyncWorkQueueCell` op is synchronous and every reader body resolves with `Task.FromResult`.
  Only `HeadAsync`/`LenAsync`/`ReadStreamAsync`/`PendingLenAsync` and friends are Task-typed,
  because an `AsyncComputed<T>` read is Task-typed by construction. The 3x3 conformance runner
  bridges that with `GetAwaiter().GetResult()` rather than pretending the plane is synchronous.

- **The queue family is a core plus three shells.** `QueueCore` / `TopicCore` / `WorkQueueCore`
  own the transitions and perform NO graph write; each mutator returns which reader kinds it
  dirtied and the shell bumps exactly those version sources inside one `Batch`. That is what makes
  "the three flavors obey one contract" structural rather than three copies agreeing by hand — the
  same split `IngressCore` already makes.

- **Ingress reader invalidation is a version-source bump, not a slot clear.** lazily-rs clears
  reader slots directly (`clear_slots`, or `batch()` on the thread-safe context). This binding
  exposes no slot-clearing surface, so each of a scope's four reader kinds — and each of the three
  receipt channels — is a `Computed` gated by its own `Source<int>` version, and an invalidation is
  a guarded write inside one `Batch`. That is the idiom `TopicCell` and `WorkQueueCell` already use;
  one batch is still one frontier walk, which is the property the per-flavor frontier-walk gates in
  `IngressCellTests` pin. There is no observer registry: nothing survives an invalidation, so
  nothing has to be unsubscribed.

## Not yet implemented

Everything in the feature matrix except the reactive graph, the merge algebra, and the
concurrency layers (thread-safe and async contexts). Do not mark a row shipped in lazily-spec's
`coverage.json` until its conformance corpus replays here with an empty ledger.

The remaining phases and the corpus each must replay are tracked in
`tasks/software/lazily-cs-parity-plan.md` in the operator workspace.

<!-- tsift:code-navigation v=0.1.80 -->
## Code Navigation

Run `tsift status` at session start from the owning repo root. If the task or file lives under a git submodule (for example `src/tsift/...`), switch to that submodule root first so the harness loads the narrower local instructions and repo state instead of the superproject root. If status prints a `run:` recommendation for stale or missing tsift state, run `tsift status --fix` before relying on tsift results; when the harness cannot perform write commands, ask the user to run the printed command instead.

Prefer tsift envelopes over raw reads:
- `tsift --envelope search <query>` instead of `grep`/`rg`
- `tsift --envelope source-read <file>` / `tsift --envelope symbol-read <symbol>` instead of `cat`/`head`
- `tsift --envelope explain <symbol>` and `tsift graph <symbol> --callers` / `--callees` for call graphs
- `tsift diff-digest [path]` instead of `git diff`, `git show`, or patch-style `git log`
- `tsift --envelope session-review <path>` / `tsift --envelope context-pack <path>` instead of replaying long session docs, transcripts, or runtime logs
- `tsift --envelope digest-runner --kind test|log --path . --shell-command '<command>'` instead of raw test/build output

Command detail lives in [`runbooks/code-navigation.md`](runbooks/code-navigation.md) — budgets, `tsift workflow search`, `report.scale_guard` handling, the harness rewrite path for `PreToolUse`-less harnesses, and Codex/OpenCode integration. `tsift init` writes and versions that runbook alongside this block, so it is present in every initialized checkout; read it before broad exploration instead of expanding this block. A repository that also ships a current `.claude/skills/tsift/SKILL.md` should use that skill as the deeper source.

For local verification, run `make check` before committing. After local changes, check the latest GitHub Actions CI run with `gh run list --workflow CI --limit 1` and fix any failing tests before calling the work complete.

Only read full source files when tsift results are insufficient.
<!-- /tsift:code-navigation -->
