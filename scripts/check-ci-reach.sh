#!/usr/bin/env bash
# CI-reachability guard (#lzcheckcireachguard).
#
# Fails the build when `make check` runs a gate that CI never reaches. That is the
# drift this guard exists for: someone adds a target to `check`, it passes locally
# forever, and no CI job ever executes it — which is exactly how #lzinteroppeerci
# happened. The interop peer, the single cross-binding wire-compatibility gate, was
# in every binding's `check` and in no binding's workflow, for months.
#
# It also exists because the obvious hand-audit is WRONG. Grepping the workflows
# for "make check" reported all nine bindings as covered; every one of those hits
# was a COMMENT. Comments are the reason this is a script and not a convention:
# only `run:` bodies count here, and comment lines inside them are stripped before
# anything is matched.
#
# WHAT IT PROVES
#
#   For every target in `check`'s prerequisite closure, at least one CI `run:`
#   step invokes the same program with the same distinguishing flags.
#
# WHAT IT DOES NOT PROVE
#
#   That CI runs it against the same inputs, in the same environment, or that the
#   command means the same thing there. Reach is a floor, not equivalence. The
#   sibling guards (conformance-coverage, assertion-keys, scenario-coverage) are
#   what prove a run examined anything.
#
# HOW A TARGET IS MATCHED
#
#   Recipes are read through `make -n`, so make variables are already expanded and
#   we compare real command lines rather than source text. `make -p` is
#   deliberately NOT used: it dumps the entire environment to stdout, which would
#   print every secret in the job's env into the CI log.
#
#   Each command is split on the shell's sequencing operators, redirections are
#   dropped, and the remainder is reduced to an ANCHOR: the program basename plus
#   its subcommands and flag NAMES (values dropped), with path arguments reduced to
#   basenames and bare path globs discarded. A target is reached when EVERY one of
#   its anchors is a subsequence of some CI command's token list, or when CI runs
#   `make <target>` directly. Every, not any: a target that runs two gates and is
#   half-covered by CI is a gap, and "any" would report it green.
#
#   Keeping flag names in the anchor is what makes the guard falsifiable rather
#   than decorative: `go test -race` does not match a CI step that only runs
#   `go test -count=1`, so dropping the race job reddens this guard instead of
#   being absorbed by the plain test job.
#
#   An argument that is still a VARIABLE reference at this point — `$MANIFEST` in
#   a CI step, or a `$$VAR` a recipe leaves for the shell — names a value the
#   guard cannot resolve, so it becomes a WILDCARD matching exactly one token on
#   the other side (#lzcireachvaranchor). Make and CI routinely spell the same
#   path differently, one through an expanded `$(VAR)` and the other through the
#   environment, and they are the same command. Dropping the token instead, which
#   is what this used to do, lost the argument as well as its value and reported
#   a step that genuinely ran the gate as unreachable — a false RED that cost one
#   binding a hardcoded second spelling of the path plus a hand-written equality
#   assertion, which is a new drift surface invented to satisfy a guard whose job
#   is detecting drift. Arity still counts: `script.sh $A` does not match a CI
#   step that passes no argument at all.
#
#   Commands whose program is a shell builtin or a plain file/text utility carry no
#   gate, so they contribute no anchor. A target with no non-trivial command at all
#   (a mkdir-only reset step, say) is reported as carrying no gate and is not
#   required to appear in CI. It cannot fail a build, so it cannot hide one.
#
# THE EXCUSE LIST IS THE OTHER HALF OF THE DELIVERABLE
#
#   scripts/ci-reach.conf names the workflows that count and the targets that are
#   deliberately local-only, each with a reason. It is the one place a reader can
#   see what this binding does not enforce in CI, in the same spirit as
#   KNOWN_UNCOVERED. Excuses are checked in ALL THREE directions: an excused target
#   that CI turns out to reach fails, so the list cannot rot into a list of things
#   that used to be true; and an excuse naming a target that is not in the closure
#   at all fails, because it excuses nothing and this guard used to ignore it in
#   silence (measured, exit 0, `0 excused`) -- the same reverse check
#   KNOWN_UNCOVERED already makes against the canonical corpus.
#
# THE OBLIGATION SET IS PINNED (#pinreachclosure)
#
#   Everything above is a claim about the CLOSURE of $ROOT_TARGET. Three separate
#   things went unpinned, and each was measured at exit 0 on a byte-identical copy
#   of this repo's Makefile before any of this existed.
#
#   (1) WHICH TARGETS ARE IN IT. Delete `test` from `check:`'s prerequisite list:
#
#           check-ci-reach: OK -- 8 target(s) reached by CI, 0 excused, 1 carrying no gate
#           exit 0
#
#       and the target that left is never named. `EXPECTED_CLOSURE_TARGETS` is
#       compared by SET EQUALITY, both directions reported separately.
#
#       SET EQUALITY, not a count. Also measured: replacing `test` with `restore`
#       in the same list reprints the healthy verdict verbatim --
#       `OK -- 9 target(s) reached by CI, 0 excused, 1 carrying no gate` -- so any
#       floor or ceiling on the tally passes a swap. The property that matters is
#       fails-when-stale, which is why this is a set and why the name carries the
#       `EXPECTED_` prefix these repos already use for exact equality.
#
#   (2) WHETHER THE CLOSURE DESCRIBES WHAT MAKE RUNS. `prereqs_of` awk-scans
#       Makefile SOURCE TEXT for the first `^$ROOT_TARGET:` line and `exit`s. It
#       never asks make, so it cannot see a conditional. Measured here, both forms:
#
#           ifeq ($(SKIP_SLOW),)
#           check: ... test ...        # the only ^check: line awk ever reads
#           else
#           check: ...                 # what make parses under SKIP_SLOW=1
#           endif
#
#       With `SKIP_SLOW=1`, `make -n check` runs ZERO `dotnet test` commands and
#       the guard's whole verdict is byte-identical (`cmp -s`) to healthy, exit 0.
#       `ifeq (0,1)` does the same permanently, committed, with no variable set.
#       A names-only pin passes both BY CONSTRUCTION, because the awk closure is
#       the same in the honest and the compromised state. So the pin sits on top of
#       a make-derived ORACLE: every anchor a closure member's own recipe carries
#       must appear in `make -n $ROOT_TARGET`'s command list. `make -n` only --
#       never `make -p`, which builds the default goal and dumps the environment.
#
#       WHY ANCHORS AND NOT RAW COMMAND LINES. The reference probe for this rung
#       compares raw `make -n` output lines with `grep -qxF`. Ported verbatim it is
#       a FALSE RED here, on a pristine tree, against `test` and
#       `conformance-coverage` -- the two targets that carry the entire conformance
#       evidence chain. Cause: `CONFORMANCE_RUN_ID := $(shell uuidgen ...)` is
#       re-evaluated per make INVOCATION, so the recipe's first physical line is
#       `LAZILY_CONFORMANCE_RUN_ID=8323ec8e-... \` out of `make -n test` and
#       `LAZILY_CONFORMANCE_RUN_ID=fa84b8a3-... \` out of `make -n check`. Those
#       lines can never be equal, by design -- the nonce is #lzstalemanifest's fix.
#       Raw-line containment is therefore structurally unusable in this binding, so
#       the oracle runs both sides through this script's own `anchors()`, which
#       drops leading VAR= assignments and flag values and joins continuations. A
#       false red here would cost exactly what this script's header already records
#       costing lazily-cpp: a second hand-maintained spelling of a recipe, invented
#       to satisfy a guard whose job is detecting drift.
#
#   (3) WHICH BUCKET EACH TARGET LANDS IN. Keeping a target's NAME and neutering
#       its recipe leaves membership intact and silently reclassifies it to
#       `no gate` -- a bucket this script does not require CI to reach at all.
#       `EXPECTED_NO_GATE_TARGETS` pins that bucket by set equality too. Today it
#       holds only the root, whose recipe is one `echo`.
#
#   WHAT A SET PIN STRUCTURALLY CANNOT SEE. Two gaps, both measured here at
#   exit 0, recorded so nobody reads a green line as more than it is:
#
#     ORDER. A set has none. Moving `conformance-coverage` ahead of `test` in
#     `check:`'s prerequisite list makes `make -n check` really run the coverage
#     guard BEFORE the suite that writes the evidence it reads, and all three
#     rungs pass; the only trace is that two `reached` lines swap places, which
#     is not a finding. What actually fails that state is a DIFFERENT guard --
#     the coverage script's run-id rung (#lzstalemanifest) -- not this one.
#
#     EDGES. A node set does not pin who depends on whom. Reparenting
#     `conformance-coverage` from `check:` to `test:` leaves the closure's node
#     set identical, so the pin, the oracle and the bucket pin all pass. An edge
#     can also be dropped with the node set unchanged whenever another member
#     already pulls the dependency in.
#
#   THE CEILING ON ALL OF THIS. The claim these rungs make is "no SILENT change",
#   never "correct". They cannot name a gate that never existed, and written
#   against an already-broken configuration they would faithfully pin the
#   breakage. That matters more here than in the bindings whose CI runs
#   `make check`: this guard is the only thing in cs that ties the gate list to
#   anything, so its pins are a change-detector on a state nobody has
#   independently verified, not a proof that the state is right.
#
#   WAS OUT OF SCOPE, NOW CLOSED (#reversereachdirection): swapping a target's
#   recipe for a DIFFERENT gate CI already runs. Every count stays put, every
#   bucket stays put, and the verdict is byte-identical, so it defeats all three
#   rungs above -- measured on this repo with `ffi-check:`'s recipe replaced by
#   `$(DOTNET) restore`, a real ci.yml step no closure member runs: exit 0,
#   `cmp`-identical to healthy, with check-ffi.sh run ZERO times by `make check`.
#   Pointing the same recipe at another MEMBER's gate measures identically.
#
#   What was rejected as the fix, and still is, is a per-target RECIPE anchor
#   hardcoded here: a second spelling of every recipe, churning at recipe rate,
#   which is the drift surface the paragraph above says not to invent. What closes
#   it instead is the reverse pin -- `EXPECTED_GATE_STEPS`, rung D -- which pins
#   the NAME of the CI step that runs each gate and asks for the member's anchors
#   INSIDE that step instead of anywhere in the workflow. Its churn is
#   step-name-rate: a recipe gaining a flag moves the recipe and the step body
#   together and the mapping does not move. See the array's own comment for the
#   measurements and for why the rung asserts step-name uniqueness instead of
#   qualifying by job.
#
#   THE MODE IS PINNED TOO, and it had to be. The step pin alone left the
#   anchor-reached POPULATION fixed only as its own complement: a member with no
#   entry was read as make-invoked by observation. A two-part edit that moved both
#   halves together -- a CI step body changed to `make <member>` AND that member's
#   entry deleted -- cancelled at exit 0 with two integers on an OK line as the
#   only trace, and put the member back within reach of the repoint above. Each
#   half alone exited 1. `EXPECTED_MAKE_INVOKED_TARGETS` pins the other population
#   as a SET, both directions, disjoint from the step pin by construction, and its
#   findings are reported BEFORE the membership ones because a mode change is the
#   cause and "no step pinned" is the symptom. A population pinned only as the
#   complement of another pinned population is not pinned, and a count is not a
#   pin.
#
#   WHY THE ORACLE IS `make -n`, AND NOT THE SET OF GATES ci.yml INVOKES. Worth
#   settling explicitly in this binding, because ci.yml here invokes exactly ONE
#   make target (`make package-check`) and spells every other gate as a direct
#   command, so make's closure and CI's step list are nearly disjoint. Measured:
#   ci.yml is byte-identical (`cmp`) under every attack in this class -- the
#   dropped prerequisite, the swap, the `ifeq`, the dead branch -- so a pin or
#   oracle derived from CI's commands is satisfied by construction in exactly the
#   states these rungs exist to catch. It is also the wrong side on principle:
#   CI's command set is this guard's HAYSTACK, and cross-checking the needle set
#   against the haystack compares CI to itself. The converse needs nothing new --
#   deleting the FFI step from ci.yml alone already exits 1 today with
#   `MISSING ffi-check` -- because reach is required of every closure member. So
#   pinning the closure pins CI's gate set transitively, and pinning CI's side
#   pins nothing new. The pin also lands in CI here despite `make check` never
#   running there, for the same reason: CI runs THIS SCRIPT directly, and the
#   script reads the Makefile.
#
#   NOT ADDED, because measurement said misdiagnosis rather than false green: the
#   reverse oracle direction, a command in `make -n $ROOT_TARGET` that no closure
#   member accounts for. Measured both ways of producing one -- a second
#   `check: flag-hygiene` rule line (awk `exit`s after the first `^check:` line, so
#   no conditional is needed) and an `ifeq` whose read branch is the shorter one --
#   and the guard already exits 1 in both, because `own_commands` is make-derived:
#   the unattributed command falls into the ROOT's own bucket and is reported as
#   `MISSING check` / `no CI run: step matches \`python3 check-flag-hygiene.py\``.
#   Wrong target named, right refusal. A fourth rung there would buy a better
#   message, not a caught escape.
set -euo pipefail

# A step that reaches a gate is still inert when its workflow or job does not
# run. Keep that outer activation proof separate from the mature command/Makefile
# reach proof below, and exercise its mutation regressions on every invocation.
python3 scripts/check-ci-activation.py --self-test
python3 scripts/check-ci-activation.py --config scripts/ci-activation.conf --guard scripts/check-ci-reach.sh

MAKE_BIN="${MAKE:-make}"
ROOT_TARGET="${CI_REACH_ROOT_TARGET:-check}"
CONF="${CI_REACH_CONF:-scripts/ci-reach.conf}"

# ---- The pinned obligation set (#pinreachclosure) -----------------------
#
# The root target these pins were AUTHORED against. Both sets below are only
# meaningful relative to one root -- a closure computed from a different root is a
# different obligation set, and reporting it against these pins would be a
# category error -- so a `CI_REACH_ROOT_TARGET` override is refused rather than
# silently re-scoped.
#
# Honest accounting of the two directions, because they measured differently:
#
#   * RENAMING `check:` was already caught. On a byte-identical copy with `check:`
#     renamed to `verify:` -- `.PHONY` still names `check`, so `make -n check`
#     succeeds with nothing to do -- the guard exits 1 through the vacuity rung,
#     `'check' has no prerequisite target carrying a gate -- nothing was verified`,
#     which is also the correct diagnosis. This constant adds nothing there.
#   * The OVERRIDE was a real false green. Measured pre-fix:
#     `CI_REACH_ROOT_TARGET=build ./scripts/check-ci-reach.sh` exits 0 with
#     `OK -- 1 target(s) reached by CI, 0 excused, 0 carrying no gate`. One env var
#     re-scopes the guard to a one-target closure and it still prints a clearance.
#     Nothing in this repo consumes `CI_REACH_ROOT_TARGET`, so refusing an unpinned
#     root costs no caller.
EXPECTED_ROOT_TARGET="check"

# Every target in `make $EXPECTED_ROOT_TARGET`'s prerequisite closure, INCLUDING
# the root itself, sorted. Compared by set equality; see the header for the two
# measurements that rule out a count.
#
# This is the closure, not the reached set, so it holds targets from every bucket
# the report has: the nine gates CI reaches, the root (which carries no gate --
# its recipe is one `echo`), and any excused target. An excuse changes whether CI
# must reach a target, never whether `make check` runs it, so excusing something
# does not take it out of this list.
#
# Two remedies when this fails, and they are not interchangeable -- restoring a
# dropped prerequisite is the finding, editing this list is a decision. The
# failure message spells both out; do not reach for the second reflexively.
EXPECTED_CLOSURE_TARGETS=(
	assertion-ordering-check
	build
	check
	ci-reach
	conformance-coverage
	ffi-check
	format-check
	interop-peer-check
	package-check
	test
)

# Closure members that legitimately carry NO gate, i.e. whose recipe runs nothing
# this guard can require of CI. Pinned by set equality because `no gate` is the one
# bucket that is never required to appear in CI, which makes it the bucket to move
# a target INTO. Measured: replacing the `test:` recipe body with `true` keeps the
# name, keeps membership, reclassifies it to `no gate`, and exits 0.
#
# Today this is only the root, whose recipe is a single `echo`. A target arriving
# here is either a real reclassification you meant, or a recipe that stopped doing
# anything.
EXPECTED_NO_GATE_TARGETS=(
	check
)

# ---- The CI STEP that runs each gate, pinned by name (#reversereachdirection) --
#
# `target|step name`, one per anchor-reached closure member. What this closes is
# the case the header above records as OUT OF SCOPE: repoint a member's recipe at
# a gate some OTHER real CI step runs and every rung to this point is satisfied,
# because `anchor_reached` asks whether SOME command in a flat set of every `run:`
# body in the counted workflows carries the anchor. Measured on this repo, pre-fix,
# with `ffi-check:`'s recipe replaced by `$(DOTNET) restore` — a real ci.yml step
# (`restore`) that no closure member runs:
#
#     check-ci-reach: OK — 9 target(s) reached by CI, 0 excused, 1 carrying no gate
#     exit 0, `cmp`-identical to the healthy report, and `make -n check` runs
#     check-ffi.sh ZERO times.
#
# Scoping reach to the member's OWN step closes it: the repointed anchor is no
# longer in `FFI surface check (#lzinteroppeerci)`, so it exits 1 and NAMES the
# member, its step, and the anchor that left. Repointing at a step no member runs,
# or at another member's gate, fails the same way.
#
# The SAME flat set leaks in the other direction, which this rung also closes.
# Measured pre-fix: delete ci.yml's `test` step and leave only `Conformance replay
# executed, zero skips`, whose command is `dotnet test ... --filter
# FullyQualifiedName~Conformance`. Its anchor is a strict superset of `test:`'s, so
# the subsequence match still holds and the guard printed the healthy verdict at
# exit 0 -- the full suite gone from CI, credited to a step that runs one filtered
# slice of it. With the step pinned, `test` resolves to the step named `test`, which
# no longer exists, and it exits 1.
#
# WHY THIS PIN AND NOT A RECIPE PIN. A per-member pin of the recipe's own anchors
# would close the same attack and was rejected: its churn is RECIPE-rate, so every
# flag added to a gate would edit it, and a pin edited reflexively is the
# passes-when-stale check this family has already removed once. This pin's churn is
# STEP-NAME-rate. A recipe gaining a flag moves the recipe and the CI step body
# together and the mapping does not move at all.
#
# WHAT IT DOES NOT COVER, and why that is not a loosening: a member CI invokes
# through `make <target>` has no independent CI-side spelling of its gate, so a
# step name pinned for it would assert nothing — CI's instruction is "run the
# target", and after a repoint CI faithfully runs whatever the target now runs.
# Those members are reported separately and are REFUSED a pin rather than mapped
# to the step that runs make. In this binding that is exactly one, `package-check`.
#
# UNIQUENESS AMONG THE PINNED NAMES, not job qualification. Step names are not
# globally unique here: of 23 `run:` steps across both workflows, 21 names are
# distinct, and the two that collide — `Fetch canonical lazily-spec conformance
# fixtures` and `Guard — canonical fixtures present` — are each one copy in ci.yml
# and one in release.yml. Both collisions are therefore CROSS-workflow, into a
# workflow ci-reach.conf deliberately does not count, and neither name carries an
# anchor at all (one is a `git clone`, the other all `test`/`echo`/`exit`). Inside
# this rung's universe all 14 counted step names are distinct.
#
# So the rung asserts that every PINNED step name occurs EXACTLY ONCE among the
# counted workflows' run: steps and refuses a duplicate, rather than growing a job
# qualifier that nothing here needs. Measured, so the claim is not larger than it
# is: adding release.yml to the conf does NOT refuse — the two duplicated names are
# not pinned, so nothing resolves ambiguously and the run exits 0. What refuses is a
# duplicate of a name a gate is pinned to, measured by giving ci.yml a second step
# called `FFI surface check (#lzinteroppeerci)`. Release.yml still must not be
# counted, for the reason ci-reach.conf already gives, and this rung is not the
# thing keeping it out.
#
# ONE EXISTING MESSAGE MOVES. Deleting the FFI step from ci.yml exited 1 before this
# rung with `MISSING ffi-check`; it now exits 1 earlier, at the step-existence
# check, with `pins 'ffi-check' to a CI step named ... and no run: step has that
# name`. Same state, same refusal, and the new wording names what actually changed.
EXPECTED_GATE_STEPS=(
	"assertion-ordering-check|Assertion observation ordering (#lzassertordering)"
	"build|build"
	"ci-reach|CI-reachability guard (#lzcheckcireachguard)"
	"conformance-coverage|Conformance coverage + scenario replay ledger"
	"ffi-check|FFI surface check (#lzinteroppeerci)"
	"format-check|dotnet format --verify-no-changes"
	"interop-peer-check|Interop peer self-check (#lzinteroppeerci)"
	"test|test"
)

# ---- The MODE each gate is reached in, pinned by set equality -----------------
#
# Closure members CI reaches by invoking `make <target>` rather than by spelling
# the gate. These are exactly the members EXPECTED_GATE_STEPS must NOT hold an
# entry for, and the two arrays are checked disjoint below.
#
# WHY THIS EXISTS, measured on this repo AFTER the step pin landed and found live
# in it. The step pin fixed which STEP runs each anchor-reached gate, and left the
# anchor-reached POPULATION fixed only as the complement of itself: a member with
# no entry was read as make-invoked by observation alone. So a two-part edit that
# moves both halves together cancels:
#
#   (1) change `FFI surface check (#lzinteroppeerci)`s body from
#       `./scripts/check-ffi.sh` to `make ffi-check`, and
#   (2) delete ffi-check from EXPECTED_GATE_STEPS.
#
# Each half alone exits 1 -- (1) as a gate pinned to a step CI reaches through
# make, (2) as a gate with no entry. TOGETHER, measured: exit 0, the only trace an
# OK line reading `7 gate(s) ... 2 invoked through make` instead of `8 ... 1`. The
# CI step that runs this guard checks exit status and greps for
# `check-ci-reach: OK`, so it passes on the runner too. And the retirement is not
# bookkeeping: with ffi-check moved into the unpinned mode, the original recipe
# repoint works again on it -- measured, `$(DOTNET) restore` in its recipe, exit 0,
# `make -n check` running check-ffi.sh ZERO times.
#
# THE GENERAL FAULT, and it is the one this family keeps re-finding: a population
# pinned only as the complement of another pinned population is not pinned against
# an edit that moves both together. A COUNT IS NOT A PIN -- that OK line held the
# only difference and held it as two integers. So the mode is pinned as a SET, both
# directions, and retiring a gate from the step-pinned population now costs three
# reviewable changes that each name the target: the CI step body, the
# EXPECTED_GATE_STEPS deletion, and the addition here.
#
# The LOADED form of the same commit, measured against the version before this
# array existed: the three edits above PLUS the recipe repointed at another
# command. Exit 0, `check-ci-reach: OK` printed for the CI step to grep, and
# `make -n check | grep -c check-ffi` -> 0. The gate ran nowhere, local or CI.
#
# TWO PROPERTIES, NOT ONE, and each falsified on this repo rather than reasoned:
#
#   (1) SET-EQUAL IN BOTH DIRECTIONS. The array is data; the equality is the check.
#       Removing the observed-but-not-pinned half alone returns the two-part edit to
#       exit 0 with the healthy verdict. Removing the pinned-but-not-observed half
#       alone returns a second real state to exit 0 -- `package-check` excused AND
#       its `make package-check` step deleted, where the entry here then asserts
#       nothing and no other rung notices. Both halves are load-bearing.
#
#   (2) MUTUALLY EXCLUSIVE WITH `EXPECTED_GATE_STEPS`, so every gate-carrying
#       non-excused member is in exactly one array and neither can absorb what the
#       other drops. Disjointness is refused up front; totality is asserted against
#       a population accumulated BEFORE the branch that classifies it, so it is a
#       statement and not a property of that branch. Falsified both ways by breaking
#       the classification: a member recorded in neither mode, and one recorded in
#       both, each exits 1.
#
# py reaches the same protection by pinning the gate-step DOMAIN set-equal to
# {gate-carrying} - {excused} - {make-invoked}. That is this, with the subtraction
# in place of the second array; the partition assertion below is what makes the two
# spellings equivalent here.
#
# NOT excused members, and not no-gate members. An excuse removes the reach
# requirement altogether and a no-gate recipe has nothing to reach, so neither is
# classified into a mode; `EXPECTED_NO_GATE_TARGETS` and ci-reach.conf own those.
EXPECTED_MAKE_INVOKED_TARGETS=(
	package-check
)

if [ ! -f Makefile ]; then
	echo "check-ci-reach: no Makefile in $(pwd)" >&2
	exit 1
fi

# ---- The dry run has to WORK before its output means anything (#lzgrepcpipefail)
#
# Every anchor this guard compares comes out of `dry_run`, which is
# `make -n "$@" 2>/dev/null | grep -v ... || true`. That `|| true` is indiscriminate:
# it was put there because `grep -v` legitimately exits 1 when a recipe's whole
# output is make's own noise, but MAKE's failure leaves through the same pipeline,
# `2>/dev/null` throws away what make said about it, and the empty stdout that
# results is then read one layer up as "this recipe runs no checkable command".
#
# Measured, on a byte-identical copy of this repo's Makefile with one line changed
# (`test:` given a prerequisite with no rule, which is all it takes):
#
#     make -n test  -> exit 2, "No rule to make target 'build/nonexistent-prereq'"
#     the guard     -> `no gate  test   recipe runs no checkable command`
#                      `check-ci-reach: OK — 8 target(s) reached, 0 excused, 2 carrying no gate`
#                      exit 0
#
# A FALSE GREEN: 9 reached became 8, 1 no-gate became 2, and the target that
# records the entire conformance evidence chain silently stopped being required to
# appear in CI — described as running no checkable command while its recipe runs
# the whole suite. The vacuity guard at the bottom does not see it; that one fires
# only when the tally reaches ZERO, which is the TOTAL-failure case (measured: a
# Makefile that does not parse exits 1 there, correctly). Partial failure is the
# hole, and nothing in CI closes it either: CI invokes exactly one make target
# (`make package-check`) and runs every other gate as a direct command, so a
# broken prerequisite on `test:` reddens no CI step at all. Only a local
# `make check` would catch it, and this guard exists precisely because local-only
# enforcement is what rots.
#
# So the dry run is gated ONCE, up front, in the MAIN shell. Not inside `dry_run`:
# that function is called from `$(dry_run ... | wc -l)` and from
# `$(own_commands ... )`, where an `exit` kills only the subshell and the script
# carries on with the very empty output it was meant to refuse. And make's stderr
# is deliberately NOT suppressed here — the message is the diagnosis.
#
# This is not a `|| true` site. A dry run that failed is missing EVIDENCE about
# what `make $ROOT_TARGET` runs, never evidence that it runs nothing.
if ! "$MAKE_BIN" -n "$ROOT_TARGET" >/dev/null; then
	echo >&2
	echo "check-ci-reach: \`$MAKE_BIN -n $ROOT_TARGET\` FAILED (see its message above)." >&2
	echo "      Every anchor this guard compares is read out of that dry run, and a" >&2
	echo "      dry run that failed produces no commands for the target it died on —" >&2
	echo "      which this guard would otherwise report as a target 'carrying no gate'" >&2
	echo "      and stop requiring in CI. That is a false green, so it refuses here." >&2
	echo "      Fix the Makefile; a partial failure silently shrinks this guard's" >&2
	echo "      reach count instead of failing it (#lzgrepcpipefail)." >&2
	exit 1
fi

# ---------------------------------------------------------------- configuration

workflows=()
workflow_count=0
excused_targets=()
excused_reasons=()
excuse_count=0

if [ -f "$CONF" ]; then
	while IFS= read -r line || [ -n "$line" ]; do
		line="${line%%$'\r'}"
		case "$line" in
		'#'* | '') continue ;;
		esac
		key="${line%%:*}"
		val="${line#*:}"
		val="$(printf '%s' "$val" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
		case "$key" in
		workflow)
			workflows+=("$val")
			workflow_count=$((workflow_count + 1))
			;;
		excuse)
			tgt="${val%%[[:space:]]*}"
			reason="${val#"$tgt"}"
			reason="$(printf '%s' "$reason" | sed -e 's/^[[:space:]]*//')"
			if [ -z "$reason" ]; then
				echo "check-ci-reach: excuse for '$tgt' has no reason — an excuse without a reason is not an excuse" >&2
				exit 1
			fi
			excused_targets+=("$tgt")
			excused_reasons+=("$reason")
			excuse_count=$((excuse_count + 1))
			;;
		*)
			echo "check-ci-reach: unknown key '$key' in $CONF" >&2
			exit 1
			;;
		esac
	done <"$CONF"
fi

if [ "$workflow_count" -eq 0 ]; then
	workflows=(".github/workflows/ci.yml")
	workflow_count=1
fi

for wf in "${workflows[@]}"; do
	if [ ! -f "$wf" ]; then
		echo "check-ci-reach: workflow '$wf' listed in $CONF does not exist" >&2
		exit 1
	fi
done

# ------------------------------------------------------- make target extraction

# A Makefile may set .RECIPEPREFIX to something other than tab (lazily-rs uses
# `>`), which puts recipe lines at column 0 where a rule line lives. Without this
# a recipe such as `>cargo test --features a:b` reads as a rule named `>cargo`.
RECIPE_PREFIX="$(awk -F= '/^[[:space:]]*\.RECIPEPREFIX[[:space:]]*[:+]?=/ {
	v = $2; gsub(/^[[:space:]]+|[[:space:]]+$/, "", v); if (v != "") print substr(v, 1, 1); exit
}' Makefile)"

# Prerequisites of a target, straight from the Makefile source, with `\`
# continuations joined and trailing comments removed. Order-only prerequisites are
# dropped: they constrain ordering, not what runs.
prereqs_of() {
	awk -v target="$1" -v rp="$RECIPE_PREFIX" '
		BEGIN { pat = "^" target ":([^=]|$)"; if (rp == "") rp = "\t" }
		{
			line = $0
			# Only the ACTUAL recipe prefix marks a recipe line. Treating any
			# leading whitespace as one loses a rule that is merely indented,
			# which under a non-tab .RECIPEPREFIX is perfectly legal make and
			# collapses the whole closure to a single target. A continuation is
			# exempt: under the default tab prefix a wrapped prerequisite list is
			# normally tab-indented.
			if (!cont && substr(line, 1, 1) == rp) next
			sub(/^[[:space:]]+/, "", line)
			if (cont) {
				buf = buf " " line
				if (line ~ /\\[[:space:]]*$/) next
				cont = 0
				emit(buf)
				exit
			}
			if (line !~ pat) next
			buf = line
			if (line ~ /\\[[:space:]]*$/) { cont = 1; next }
			emit(buf)
			exit
		}
		function emit(s,   rest, n, i, parts) {
			gsub(/\\/, " ", s)
			sub(/#.*$/, "", s)
			rest = substr(s, index(s, ":") + 1)
			sub(/\|.*$/, "", rest)
			n = split(rest, parts, /[[:space:]]+/)
			for (i = 1; i <= n; i++) if (parts[i] != "") print parts[i]
		}
	' Makefile
}

# Is this name an explicit rule in the Makefile?
is_makefile_target() {
	awk -v target="$1" -v rp="$RECIPE_PREFIX" '
		BEGIN { pat = "^" target ":([^=]|$)"; if (rp == "") rp = "\t"; found = 0 }
		substr($0, 1, 1) == rp { next }
		{ line = $0; sub(/^[[:space:]]+/, "", line) }
		line ~ pat { found = 1; exit }
		END { exit found ? 0 : 1 }
	' Makefile
}

# Breadth-first closure of ROOT_TARGET's prerequisites, parents before children.
closure=""
queue="$ROOT_TARGET"
seen=" "
while [ -n "$queue" ]; do
	current="${queue%%$'\n'*}"
	if [ "$current" = "$queue" ]; then queue=""; else queue="${queue#*$'\n'}"; fi
	[ -n "$current" ] || continue
	case "$seen" in
	*" $current "*) continue ;;
	esac
	seen="$seen$current "
	closure="$closure$current"$'\n'
	while IFS= read -r dep; do
		[ -n "$dep" ] || continue
		if is_makefile_target "$dep"; then
			queue="$queue$dep"$'\n'
		fi
	done < <(prereqs_of "$current")
done

# `make -n` for a target emits its prerequisites' commands first, then its own.
# Asking make for the prerequisite list alone yields exactly that prefix — make
# applies the same de-duplication to both invocations — so removing it leaves the
# target's own recipe. Diagnostics make writes about targets it has nothing to do
# for are not commands and are dropped.
# A recipe line broken across physical lines with `\` reaches the shell as ONE
# command, and make -n prints it the way the Makefile spells it. Joining here is
# what keeps `VAR=x \` + `go test ./...` from being read as two commands, the
# second of which is where the whole gate lives.
join_continuations() {
	awk '
		{
			line = $0
			if (line ~ /\\[[:space:]]*$/) {
				sub(/\\[[:space:]]*$/, "", line)
				buf = buf line " "
				next
			}
			print buf line
			buf = ""
		}
		END { if (buf != "") print buf }
	'
}

# make's stderr is passed THROUGH rather than sent to `/dev/null`, and that is the
# whole of the change here (#lzgrepcpipefail). Only stdout is parsed, so nothing
# downstream cares; what `2>/dev/null` bought was silence about make dying, which
# was the other half of the original defect.
#
# It is deliberately NOT turned into a refusal, and the multi-goal
# `make -n "${deps[@]}"` call below is deliberately NOT probed. lazily-py's
# reasoning, which measurement here agrees with: when that invocation fails the
# target is credited with its PREREQUISITES' anchors as well, which over-reports
# and therefore fails closed. Measured on this repo with a conditional keyed on
# the dep-list goal string: the guard exits 1 either way, naming CI
# (`no CI run: step matches check-package.sh`) rather than the Makefile — a
# misdiagnosis, not a false green. Only the SINGLE-target invocation turns a
# failure into silence, so that is the one the probe in the main loop mirrors.
# Testing stderr for BYTES would also invent a false-red surface: any make that
# warns while succeeding would redden a clean tree.
dry_run() {
	"$MAKE_BIN" -n "$@" | grep -v -e '^make\[' -e '^make:' | join_continuations || true
}

own_commands() {
	local target="$1"
	local deps=()
	local dep_count=0
	while IFS= read -r dep; do
		[ -n "$dep" ] || continue
		if is_makefile_target "$dep"; then
			deps+=("$dep")
			dep_count=$((dep_count + 1))
		fi
	done < <(prereqs_of "$target")

	if [ "$dep_count" -eq 0 ]; then
		dry_run "$target"
		return
	fi
	local prefix
	prefix="$(dry_run "${deps[@]}" | wc -l)"
	dry_run "$target" | tail -n +"$((prefix + 1))"
}

# ------------------------------------------------------------- workflow scraping

# Command lines from every `run:` step. Comment lines inside a run body are
# stripped here — the whole reason this guard is a script.
#
# Two modes on top of the flat one, both for the step pin (#reversereachdirection):
#
#   --step <name>   emit only the run: bodies of steps whose `name:` is exactly
#                   <name>.
#   --list-steps    emit one line per run: step, holding that step's name (empty
#                   for an unnamed one), instead of its commands.
#
# ONE parser, parameterized, deliberately. A second awk that re-scrapes YAML for
# step boundaries is a second spelling of "what a run: body is", and the flat and
# step-scoped rungs disagreeing about that would be indistinguishable from the
# drift they exist to find. The step-name state is tracked OUTSIDE the block-scalar
# branch, so a `- name:` line that happens to appear inside a run: body is script
# text and not a step boundary.
ci_commands() {
	local want="" list=0
	while :; do
		case "${1:-}" in
		--step)
			want="$2"
			shift 2
			;;
		--list-steps)
			list=1
			shift
			;;
		*) break ;;
		esac
	done
	awk -v want_step="$want" -v list_steps="$list" '
		BEGIN { step_indent = 9999 }
		function trim(s) {
			sub(/^[[:space:]]+/, "", s)
			sub(/[[:space:]]+$/, "", s)
			# A quoted YAML scalar is the same step name as an unquoted one.
			if (substr(s, 1, 1) == "\"" || substr(s, 1, 1) == "'"'"'") {
				if (substr(s, length(s), 1) == substr(s, 1, 1)) s = substr(s, 2, length(s) - 2)
			}
			return s
		}
		function flush() { if (buf != "") { if (emit_ok) print buf; buf = "" } }
		{
			line = $0
			indent = match(line, /[^ ]/) - 1
			if (indent < 0) indent = 9999

			if (inblock) {
				if (line ~ /^[[:space:]]*$/) next
				if (indent <= block_indent) { flush(); inblock = 0 }
				else {
					sub(/^[[:space:]]+/, "", line)
					if (substr(line, 1, 1) == "#") next
					if (line ~ /\\[[:space:]]*$/) {
						sub(/\\[[:space:]]*$/, "", line)
						buf = buf " " line
						next
					}
					if (buf != "") { if (emit_ok) print buf " " line; buf = "" } else if (emit_ok) print line
					next
				}
			}

			# WHICH STEP ARE WE IN. A list item resets the name, so a step with no
			# `name:` is reported as the empty string rather than inheriting the
			# name of the one before it — crediting a pinned step with the commands
			# of an unnamed step is the false green this rung exists to refuse.
			# `name:` is only
			# read as a key: on the item line itself, or as a later key of the same
			# item, never from anywhere inside a value.
			if (line ~ /^[[:space:]]*-[[:space:]]/) {
				curstep = ""
				step_indent = indent
				if (match(line, /^[[:space:]]*-[[:space:]]+name:[[:space:]]*/))
					curstep = trim(substr(line, RSTART + RLENGTH))
			} else if (indent > step_indent && match(line, /^[[:space:]]*name:[[:space:]]*/)) {
				curstep = trim(substr(line, RSTART + RLENGTH))
			}

			if (line ~ /^[[:space:]]*(-[[:space:]]+)?run:[[:space:]]*[|>][-+]?[[:space:]]*$/) {
				inblock = 1
				block_indent = indent
				buf = ""
				emit_ok = (want_step == "" || curstep == want_step)
				if (list_steps + 0) { print curstep; emit_ok = 0 }
				next
			}
			if (line ~ /^[[:space:]]*(-[[:space:]]+)?run:[[:space:]]*[^|>[:space:]]/) {
				sub(/^[[:space:]]*(-[[:space:]]+)?run:[[:space:]]*/, "", line)
				if (list_steps + 0) { print curstep; next }
				if (want_step == "" || curstep == want_step) print line
			}
		}
		END { flush() }
	' "$@"
}

# ------------------------------------------------------------------- normalizing

# Reduce command text to anchors, one per line, each a space-separated token list.
anchors() {
	awk '
		BEGIN {
			# Sentinel for an unresolvable variable reference. Deliberately not a
			# string any real argument can be.
			ANY = "\001any"
			split(": true false echo printf cd pushd popd mkdir rmdir rm cp mv ln touch " \
			      "export unset set local read eval exec trap wait sleep exit return " \
			      "if then else elif fi for while until do done case esac function " \
			      "test [ [[ pwd ls cat head tail sed awk grep egrep fgrep sort uniq " \
			      "wc tr cut paste tee xargs env dirname basename date git", t, / /)
			for (i in t) if (t[i] != "") trivial[t[i]] = 1
		}
		{
			n = split(split_unquoted($0), cmds, /\n/)
			for (i = 1; i <= n; i++) emit(cmds[i])
		}
		# Split on the shell'"'"'s sequencing operators, but ONLY outside quotes. Doing
		# this before quotes are stripped is what stops a `;` inside a message —
		# `echo "missing $(DIR); clone the sibling"` — from being read as a second
		# command and inventing an anchor for a gate that does not exist. That is a
		# false RED, so it costs a real target its verdict.
		function split_unquoted(s,   i, c, nxt, len, inq, q, out) {
			out = ""; inq = 0; q = ""; len = length(s)
			for (i = 1; i <= len; i++) {
				c = substr(s, i, 1)
				if (inq) {
					if (c == q) { inq = 0; q = "" }
					out = out c
					continue
				}
				if (c == "\"" || c == "'"'"'" || c == "`") { inq = 1; q = c; out = out c; continue }
				nxt = substr(s, i + 1, 1)
				if (c == ";") { out = out "\n"; continue }
				if ((c == "&" && nxt == "&") || (c == "|" && nxt == "|")) { out = out "\n"; i++; continue }
				if (c == "|") { out = out "\n"; continue }
				out = out c
			}
			return out
		}
		function emit(cmd,   m, j, tok, out, prog, started, parts) {
			gsub(/[`"'"'"']/, " ", cmd)
			gsub(/\$\(/, " ", cmd)
			gsub(/\$\{/, " ", cmd)
			gsub(/[(){}]/, " ", cmd)
			m = split(cmd, parts, /[[:space:]]+/)
			prog = ""
			out = ""
			started = 0
			for (j = 1; j <= m; j++) {
				tok = parts[j]
				if (tok == "" || tok == "\\") continue
				if (tok ~ /^[0-9]*>>?$/ || tok == "<" || tok ~ /^[0-9]+>&[0-9]+$/) break
				if (!started) {
					if (tok ~ /^[A-Za-z_][A-Za-z0-9_]*=/) continue
					started = 1
					prog = tok
					sub(/.*\//, "", prog)
					if (prog == "" || (prog in trivial)) return
					out = prog
					continue
				}
				if (tok ~ /^-/) {
					sub(/=.*$/, "", tok)
					out = out " " tok
					continue
				}
				if (tok ~ /^\.{1,3}$/ || tok ~ /^\.{1,2}\/\.{0,3}$/) continue
				if (tok ~ /\//) {
					sub(/\/+$/, "", tok)
					sub(/.*\//, "", tok)
					if (tok == "" || tok ~ /^\.{1,3}$/) continue
				}
					# A token that is still a shell/make VARIABLE reference names a
					# value this guard cannot resolve — a CI step spelling a path as
					# "$LAZILY_CONFORMANCE_MANIFEST" and a Makefile recipe spelling the
					# same path through an expanded $(VAR) are the same command. Dropping
					# it (what this used to do) loses the ARGUMENT as well as its value,
					# so `script.sh <path>` no longer matched a CI step that really ran
					# `script.sh "$PATH"` and the target was reported unreachable. That is
					# a false RED, and it cost lazily-cpp a hardcoded second spelling of
					# the path plus a hand-written equality assertion to keep the two in
					# sync — a new drift surface invented to satisfy a guard that exists
					# to detect drift.
					#
					# Emit a WILDCARD instead: one token that matches one token, so arity
					# is preserved. `script.sh $A` still fails against a CI step that
					# passes no argument at all. This is the same looseness the normalizer
					# already applies to paths, which it reduces to basenames — reach is a
					# floor, not equivalence, exactly as the header says.
					if (substr(tok, 1, 1) == "$") { out = out " " ANY; continue }
				out = out " " tok
			}
			if (started && out != "") print out
		}
	'
}

# ------------------------------------------------ the pinned obligation set (B)
#
# Placed after `dry_run`/`anchors` because rung A needs them, and before any CI
# scraping because all three rungs here are statements about the MAKEFILE alone.
# A mismatch exits immediately rather than joining `status` at the bottom: every
# verdict below is a verdict ABOUT this set, and printing "9 reached" over a
# closure that is not the pinned one is exactly the clearance being withheld. The
# empty-haystack refusal just below makes the same choice.

if [ "$ROOT_TARGET" != "$EXPECTED_ROOT_TARGET" ]; then
	echo >&2
	echo "check-ci-reach: root target is '$ROOT_TARGET' but the pins in $0 were" >&2
	echo "      authored against '$EXPECTED_ROOT_TARGET'. A closure computed from" >&2
	echo "      another root is a different obligation set, so comparing it against" >&2
	echo "      these pins would report on a set nobody pinned. Re-point" >&2
	echo "      EXPECTED_ROOT_TARGET and the lists beside it in the same commit, or" >&2
	echo "      drop the override." >&2
	exit 1
fi

# A pin of nothing pins nothing -- the same vacuity rule the conformance guards
# apply (#lzvacuousrun). Guarded explicitly because emptying the array is the
# cheapest way to "fix" a failure here, and because `"${ARR[@]}"` on an empty
# array under `set -u` is a trap of its own.
if [ "${#EXPECTED_CLOSURE_TARGETS[@]}" -eq 0 ]; then
	echo "check-ci-reach: EXPECTED_CLOSURE_TARGETS is empty — a pin over no targets" >&2
	echo "      approves any closure, including one that runs nothing." >&2
	exit 1
fi

# ---- A. The make-derived oracle: does the closure describe what make RUNS? ----
#
# Compared at ANCHOR level, through this script's own normalizer, NOT as raw
# `make -n` lines: `CONFORMANCE_RUN_ID := $(shell uuidgen ...)` is re-evaluated per
# make invocation, so `test`'s and `conformance-coverage`'s recipes are spelled
# with a different nonce in `make -n <target>` than in `make -n $ROOT_TARGET` and
# raw-line containment reports both as mismatches on a pristine tree. See the
# header for the measurement.
#
# A target with no anchors at all is skipped here and handled by the no-gate pin
# below; a target whose dry run FAILED is skipped here and reported as UNREADABLE
# by the main loop (#lzgrepcpipefail), which is the finding that already owns it.
root_cmd_anchors="$(dry_run "$ROOT_TARGET" | anchors | LC_ALL=C sort -u)"
oracle_miss=""
oracle_miss_count=0
while IFS= read -r _t; do
	[ -n "$_t" ] || continue
	[ "$_t" = "$ROOT_TARGET" ] && continue
	_ta="$(dry_run "$_t" | anchors | LC_ALL=C sort -u)"
	[ -n "$_ta" ] || continue
	while IFS= read -r _a; do
		[ -n "$_a" ] || continue
		# awk over a HERE-STRING, not `printf | grep -q`: grep -q exits on the
		# first match, SIGPIPEs the writer, and under `pipefail` that inverts the
		# test into a failure. This script documents that exact inversion
		# elsewhere; do not reintroduce it here.
		if ! awk -v want="$_a" '$0 == want { f = 1; exit } END { exit f ? 0 : 1 }' <<<"$root_cmd_anchors"; then
			oracle_miss="$oracle_miss$_t   $_a"$'\n'
			oracle_miss_count=$((oracle_miss_count + 1))
		fi
	done <<<"$_ta"
done <<<"$closure"

if [ "$oracle_miss_count" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $oracle_miss_count gate(s) in the awk-derived closure that" >&2
	echo "      \`$MAKE_BIN -n $ROOT_TARGET\` does not actually run:" >&2
	while IFS= read -r _l; do
		[ -n "$_l" ] || continue
		echo "  - $_l" >&2
	done <<<"$oracle_miss"
	echo >&2
	echo "The closure is read by awk-scanning Makefile SOURCE for the first" >&2
	echo "\`^$ROOT_TARGET:\` line, which cannot see a make conditional. When the two" >&2
	echo "disagree, make wins and every verdict below describes a prerequisite list" >&2
	echo "nothing executes — measured as a byte-identical healthy report at exit 0." >&2
	echo "Look for an \`ifeq\`/\`ifdef\` around \`$ROOT_TARGET:\`, a dead branch, or a" >&2
	echo "second spelling of the rule (#pinreachclosure)." >&2
	exit 1
fi

# ---- B. Membership of the closure, pinned in BOTH directions ----
pin_sorted="$(printf '%s\n' "${EXPECTED_CLOSURE_TARGETS[@]}" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
closure_sorted="$(printf '%s' "$closure" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
# `comm` needs both sides in the same collation, hence LC_ALL=C on every one of
# them. -23 is pinned-only, -13 is closure-only.
pin_only="$(LC_ALL=C comm -23 <(printf '%s\n' "$pin_sorted") <(printf '%s\n' "$closure_sorted"))"
closure_only="$(LC_ALL=C comm -13 <(printf '%s\n' "$pin_sorted") <(printf '%s\n' "$closure_sorted"))"
# awk, not `grep -c`: `grep -c` exits 1 on a zero count and would take the script
# down through `set -e` at the one moment the number is interesting.
pin_count="$(printf '%s\n' "$pin_sorted" | awk 'NF { n++ } END { print n + 0 }')"

if [ -n "$pin_only" ] || [ -n "$closure_only" ]; then
	echo >&2
	echo "check-ci-reach: the '$ROOT_TARGET' prerequisite closure does not match" >&2
	echo "      EXPECTED_CLOSURE_TARGETS in $0." >&2
	if [ -n "$pin_only" ]; then
		echo >&2
		echo "  PINNED, but NOT IN THE CLOSURE — a gate left \`$ROOT_TARGET:\`'s prerequisites," >&2
		echo "  or was renamed. This is the drop that used to print a quieter OK line:" >&2
		while IFS= read -r _t; do
			[ -n "$_t" ] || continue
			echo "    - $_t" >&2
		done <<<"$pin_only"
	fi
	if [ -n "$closure_only" ]; then
		echo >&2
		echo "  IN THE CLOSURE, but NOT PINNED — a target was added without being pinned:" >&2
		while IFS= read -r _t; do
			[ -n "$_t" ] || continue
			echo "    - $_t" >&2
		done <<<"$closure_only"
	fi
	echo >&2
	echo "Two remedies, and they are NOT interchangeable:" >&2
	echo "  * A gate was dropped or renamed by accident — restore it to" >&2
	echo "    \`$ROOT_TARGET:\`'s prerequisite list. That is the finding this pin exists" >&2
	echo "    to surface, and editing the pin instead hides it." >&2
	echo "  * You MEANT to change what \`$MAKE_BIN $ROOT_TARGET\` runs — update" >&2
	echo "    EXPECTED_CLOSURE_TARGETS in the same commit, so the new obligation set" >&2
	echo "    is a reviewable edit rather than a silently smaller one." >&2
	exit 1
fi

# An excuse for a target that is not in the closure at all excuses NOTHING, and
# this guard used to ignore it in silence: `is_excused` is only ever consulted
# while walking closure members, so a misspelled or outdated excuse never reached
# it. Measured pre-fix with `excuse: totally-not-a-target ...` appended to the
# conf: `OK — 9 target(s) reached by CI, 0 excused, 1 carrying no gate`, exit 0.
# This is the reverse direction KNOWN_UNCOVERED already checks against the
# canonical corpus, and it is a CONFIG error, so it refuses here with the other
# config errors rather than at the verdict.
bogus_excuses=""
bogus_excuse_count=0
for _i in "${!excused_targets[@]}"; do
	_t="${excused_targets[$_i]}"
	case "$seen" in
	*" $_t "*) continue ;;
	esac
	bogus_excuses="$bogus_excuses$_t"$'\n'
	bogus_excuse_count=$((bogus_excuse_count + 1))
done
if [ "$bogus_excuse_count" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $bogus_excuse_count excuse(s) in $CONF naming a target that is not in" >&2
	echo "      \`$ROOT_TARGET\`'s prerequisite closure at all:" >&2
	while IFS= read -r _t; do
		[ -n "$_t" ] || continue
		echo "  - $_t" >&2
	done <<<"$bogus_excuses"
	echo >&2
	echo "An excuse for a target \`$MAKE_BIN $ROOT_TARGET\` never runs covers nothing, and this" >&2
	echo "guard silently ignored it. Either the target was renamed or removed — drop the" >&2
	echo "excuse, or fix the name — or the excuse was written against a target that was" >&2
	echo "never in the closure." >&2
	exit 1
fi

printf 'closure pin satisfied: %s target(s) pinned for root `%s`, all executed by `%s -n %s`\n' \
	"$pin_count" "$ROOT_TARGET" "$MAKE_BIN" "$ROOT_TARGET"

# --------------------------------------------------------------------- matching

ci_raw="$(mktemp)"
ci_anchor="$(mktemp)"
step_dir="$(mktemp -d)"
trap 'rm -f "$ci_raw" "$ci_anchor"; rm -rf "$step_dir"' EXIT
ci_commands "${workflows[@]}" >"$ci_raw"
anchors <"$ci_raw" | sort -u >"$ci_anchor"

if [ ! -s "$ci_anchor" ]; then
	echo "check-ci-reach: no run: steps found in ${workflows[*]} — a guard with an empty haystack passes everything" >&2
	exit 1
fi

# ---- D. The step pin: shape first, reach in the main loop -------------------
#
# One line per `run:` step in the counted workflows, holding that step's name.
# Read once: it is the namespace every pinned name is resolved against.
ci_step_names="$(ci_commands --list-steps "${workflows[@]}")"

# How many run: steps carry this exact name.
step_occurrences() {
	awk -v want="$1" '$0 == want { n++ } END { print n + 0 }' <<<"$ci_step_names"
}

# The step pinned for a target, or empty.
gate_step_of() {
	local t="$1" e
	for e in ${EXPECTED_GATE_STEPS[@]+"${EXPECTED_GATE_STEPS[@]}"}; do
		if [ "${e%%|*}" = "$t" ]; then
			printf '%s' "${e#*|}"
			return
		fi
	done
}

# Path to a file of the anchors of ONE named step, computed once per target.
step_anchor_file() {
	local t="$1" step="$2" key f
	key="$(printf '%s' "$t" | tr -c 'A-Za-z0-9._-' '_')"
	f="$step_dir/$key"
	if [ ! -f "$f" ]; then
		ci_commands --step "$step" "${workflows[@]}" | anchors | sort -u >"$f"
	fi
	printf '%s' "$f"
}

# A `run:` step with no `name:` cannot be pinned, so any gate it runs sits outside
# this rung permanently. Refused rather than tolerated: a `name:` is not a
# behaviour change, and the alternative is a rung whose namespace has holes in it
# that nothing reports. This binding has none today — all 14 counted run: steps are
# named.
unnamed_steps="$(awk '$0 == "" { n++ } END { print n + 0 }' <<<"$ci_step_names")"
if [ "$unnamed_steps" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $unnamed_steps run: step(s) in ${workflows[*]} have no \`name:\`." >&2
	echo "      EXPECTED_GATE_STEPS pins gates BY STEP NAME, so an unnamed step can" >&2
	echo "      never be pinned and any gate it runs is outside that rung for good." >&2
	echo "      Name the step — a \`name:\` changes no behaviour (#reversereachdirection)." >&2
	exit 1
fi

# Pin SHAPE, before any of it is trusted: no duplicate target, every pinned target
# really in the closure, every pinned step name present exactly once. All three are
# config errors, so they refuse here with the other config errors rather than at
# the verdict — a step pin resolved against a name that does not exist would redden
# a healthy tree and read as a CI gap.
_pin_seen=" "
for _e in ${EXPECTED_GATE_STEPS[@]+"${EXPECTED_GATE_STEPS[@]}"}; do
	_t="${_e%%|*}"
	_s="${_e#*|}"
	if [ "$_t" = "$_e" ] || [ -z "$_t" ] || [ -z "$_s" ]; then
		echo "check-ci-reach: malformed EXPECTED_GATE_STEPS entry '$_e' — expected \`target|step name\`" >&2
		exit 1
	fi
	case "$_pin_seen" in
	*" $_t "*)
		echo "check-ci-reach: '$_t' is pinned to more than one CI step in EXPECTED_GATE_STEPS" >&2
		exit 1
		;;
	esac
	_pin_seen="$_pin_seen$_t "
	case "$seen" in
	*" $_t "*) ;;
	*)
		echo "check-ci-reach: EXPECTED_GATE_STEPS pins a step for '$_t', which is not in" >&2
		echo "      \`$ROOT_TARGET\`'s prerequisite closure at all — the pin covers nothing." >&2
		exit 1
		;;
	esac
	_n="$(step_occurrences "$_s")"
	if [ "$_n" -eq 0 ]; then
		echo >&2
		echo "check-ci-reach: EXPECTED_GATE_STEPS pins '$_t' to a CI step named" >&2
		echo "      \`$_s\`, and no run: step in ${workflows[*]} has that name." >&2
		echo "      A step was renamed or removed. Re-point the pin if the rename was" >&2
		echo "      deliberate; restore the step if it was not (#reversereachdirection)." >&2
		exit 1
	fi
	if [ "$_n" -gt 1 ]; then
		echo >&2
		echo "check-ci-reach: EXPECTED_GATE_STEPS pins '$_t' to \`$_s\`, and $_n run: steps in" >&2
		echo "      ${workflows[*]} carry that name. A pin that resolves to more than one step" >&2
		echo "      is satisfied by whichever copy happens to spell the gate, which is the" >&2
		echo "      flat check this rung replaces. Rename one of the steps, or stop counting" >&2
		echo "      the workflow that duplicates it (#reversereachdirection)." >&2
		exit 1
	fi
done

# Same three shape checks for the mode pin, plus DISJOINTNESS. A target in both
# arrays is a contradiction -- "CI spells this gate, in this step" and "CI does not
# spell this gate" -- and the only reason to write it is to satisfy both rungs at
# once while the mode really moved.
_mode_seen=" "
for _t in ${EXPECTED_MAKE_INVOKED_TARGETS[@]+"${EXPECTED_MAKE_INVOKED_TARGETS[@]}"}; do
	case "$_mode_seen" in
	*" $_t "*)
		echo "check-ci-reach: '$_t' appears twice in EXPECTED_MAKE_INVOKED_TARGETS" >&2
		exit 1
		;;
	esac
	_mode_seen="$_mode_seen$_t "
	case "$seen" in
	*" $_t "*) ;;
	*)
		echo "check-ci-reach: EXPECTED_MAKE_INVOKED_TARGETS names '$_t', which is not in" >&2
		echo "      \`$ROOT_TARGET\`'s prerequisite closure at all — the pin covers nothing." >&2
		exit 1
		;;
	esac
	if [ -n "$(gate_step_of "$_t")" ]; then
		echo >&2
		echo "check-ci-reach: '$_t' is in BOTH EXPECTED_MAKE_INVOKED_TARGETS and" >&2
		echo "      EXPECTED_GATE_STEPS. Those say opposite things about how CI reaches it:" >&2
		echo "      one that CI spells its gate in a named step, the other that CI only runs" >&2
		echo "      \`$MAKE_BIN $_t\`. Exactly one is true (#reversereachdirection)." >&2
		exit 1
	fi
done

# Does CI contain a command whose tokens contain this anchor as an in-order
# subsequence? Extra flags and arguments on the CI side are fine; missing ones are
# not.
#
# $2 narrows the haystack to one file of anchors; it defaults to every counted
# workflow's. ONE matcher for both the flat rung and the step-scoped one, so
# "reached" cannot mean two different things depending on which asked.
anchor_reached() {
	awk -v want="$1" '
		BEGIN { ANY = "\001any"; wn = split(want, w, / /) }
		{
			hn = split($0, h, / /)
			wi = 1
			# A wildcard on EITHER side matches, because either side may be the
			# one that spelled the argument through a variable.
			for (hi = 1; hi <= hn && wi <= wn; hi++)
				if (h[hi] == w[wi] || h[hi] == ANY || w[wi] == ANY) wi++
			if (wi > wn) { found = 1; exit }
		}
		END { exit found ? 0 : 1 }
	' "${2:-$ci_anchor}"
}

# CI invoking the target through make counts as reach without any anchor work.
make_invokes() {
	awk -v target="$1" '
		{
			n = split($0, t, / /)
			if (t[1] != "make") next
			for (i = 2; i <= n; i++) if (t[i] == target) { found = 1; exit }
		}
		END { exit found ? 0 : 1 }
	' "$ci_anchor"
}

is_excused() {
	local t="$1" i
	for i in "${!excused_targets[@]}"; do
		[ "${excused_targets[$i]}" = "$t" ] && return 0
	done
	return 1
}

excuse_reason() {
	local t="$1" i
	for i in "${!excused_targets[@]}"; do
		if [ "${excused_targets[$i]}" = "$t" ]; then
			printf '%s' "${excused_reasons[$i]}"
			return
		fi
	done
}

unreached=""
unreached_count=0
stale=""
stale_count=0
nogate=""
nogate_count=0
reached=0
excused_ok=0
unreadable=""
unreadable_count=0
step_ok=0
stepmiss=""
stepmiss_count=0
makeobserved=""
anchorobserved=""
gated=""
makereached_count=0

while IFS= read -r target; do
	[ -n "$target" ] || continue

	# ---- PER-TARGET dry-run probe (#lzgrepcpipefail) --------------------------
	#
	# The up-front `make -n $ROOT_TARGET` gate above is NOT sufficient, and the
	# hole is ordinary make, not a contrivance. `$(MAKECMDGOALS)` differs between
	# the root invocation and the per-target ones this guard actually makes, so a
	# goal-conditional prerequisite is readable from the root and unreadable from
	# the member:
	#
	#     ifeq ($(MAKECMDGOALS),test)
	#     test: only-when-test-is-the-goal
	#     endif
	#
	# Measured on a byte-identical copy of this repo's Makefile plus those three
	# lines: `make -n check` exit 0 — the root gate sees nothing at all — while
	# `make -n test` exits 2. The root-only gate therefore passed it straight
	# through and the guard printed `no gate test` / `OK — 8 reached, 2 no gate`,
	# exit 0. The same false green as before, with the fix already in place.
	#
	# So every closure member is probed on its OWN goal, before its output is
	# read, and an unreadable target is counted as a FAILURE rather than excused
	# by silence. It gets its own bucket instead of joining `unreached`: the
	# finding is "this guard cannot see what the target runs", which is not the
	# same claim as "CI does not run it", and naming it wrongly is how the
	# original defect read.
	if ! probe_err="$("$MAKE_BIN" -n "$target" 2>&1 >/dev/null)"; then
		unreadable="$unreadable$target"$'\n'
		unreadable_count=$((unreadable_count + 1))
		printf 'UNREADABLE  %s\n' "$target"
		while IFS= read -r l; do
			[ -n "$l" ] || continue
			printf '           %s\n' "$l"
		done <<<"$probe_err"
		continue
	fi

	target_anchors="$(own_commands "$target" | anchors | sort -u || true)"

	if [ -z "$target_anchors" ]; then
		nogate="$nogate$target"$'\n'
		nogate_count=$((nogate_count + 1))
		continue
	fi

	hit=1
	missing_anchors=""
	invoked_via_make=0
	if make_invokes "$target"; then
		invoked_via_make=1
	else
		while IFS= read -r a; do
			[ -n "$a" ] || continue
			if ! anchor_reached "$a"; then
				hit=0
				missing_anchors="$missing_anchors$a"$'\n'
			fi
		done <<<"$target_anchors"
	fi

	if is_excused "$target"; then
		if [ "$hit" -eq 1 ]; then
			stale="$stale$target"$'\n'
			stale_count=$((stale_count + 1))
		else
			excused_ok=$((excused_ok + 1))
			printf 'excused  %-32s %s\n' "$target" "$(excuse_reason "$target")"
		fi
		continue
	fi

	if [ "$hit" -eq 1 ]; then
		reached=$((reached + 1))
		printf 'reached  %s\n' "$target"
	else
		unreached="$unreached$target"$'\n'
		unreached_count=$((unreached_count + 1))
		printf 'MISSING  %s\n' "$target"
		while IFS= read -r a; do
			[ -n "$a" ] || continue
			printf '           no CI run: step matches `%s`\n' "$a"
		done <<<"$missing_anchors"
	fi

	# ---- D. the MODE, then reach INSIDE the member's own CI step ---------------
	#      (#reversereachdirection)
	#
	# Runs after the flat verdict above, and reports separately, because the two
	# make different claims: "some counted run: step carries this anchor" and "the
	# step this gate is pinned to does". A member that fails the flat check has
	# already failed the build with the stronger finding, so its step is not
	# re-reported.
	#
	# The OBSERVED mode is recorded here and compared to EXPECTED_MAKE_INVOKED_TARGETS
	# by set equality at the bottom. Both observed sets are accumulated
	# unconditionally, so neither population is the other's complement.
	# The population both pins partition. Accumulated BEFORE the branch, so the
	# claim "every gate-carrying non-excused member is in exactly one of the two
	# arrays" is checked against a set built independently of the branch that
	# classifies it, rather than being a property of that branch.
	gated="$gated$target"$'\n'

	if [ "$invoked_via_make" -eq 1 ]; then
		makeobserved="$makeobserved$target"$'\n'
		makereached_count=$((makereached_count + 1))
		continue
	fi

	anchorobserved="$anchorobserved$target"$'\n'
	gate_step="$(gate_step_of "$target")"
	# No pin, or a mode that moved: both are findings the set-equality blocks at the
	# bottom own, and the mode one is reported FIRST because a mode change is the
	# CAUSE and a missing step pin is its symptom. Nothing to check inside a step
	# that is not pinned, so fall through.
	[ -n "$gate_step" ] || continue
	[ "$hit" -eq 1 ] || continue

	step_anchors="$(step_anchor_file "$target" "$gate_step")"
	step_missing=""
	while IFS= read -r a; do
		[ -n "$a" ] || continue
		anchor_reached "$a" "$step_anchors" || step_missing="$step_missing$a"$'\n'
	done <<<"$target_anchors"

	if [ -z "$step_missing" ]; then
		step_ok=$((step_ok + 1))
	else
		stepmiss="$stepmiss$target"$'\n'
		stepmiss_count=$((stepmiss_count + 1))
		printf 'STEP-MISS  %s\n' "$target"
		printf '           pinned CI step: `%s`\n' "$gate_step"
		while IFS= read -r a; do
			[ -n "$a" ] || continue
			printf '           not run by that step: `%s`\n' "$a"
		done <<<"$step_missing"
	fi
done <<<"$closure"

while IFS= read -r target; do
	[ -n "$target" ] || continue
	printf 'no gate  %-32s recipe runs no checkable command\n' "$target"
done <<<"$nogate"

# ---- D. the two populations, each pinned as a SET in both directions ---------
#
# `sort -u` on both sides of both comparisons, and LC_ALL=C on every one of them so
# `comm` sees one collation. -23 is pinned-only, -13 is observed-only.
mode_obs_sorted="$(printf '%s' "$makeobserved" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
mode_pin_sorted="$(printf '%s\n' ${EXPECTED_MAKE_INVOKED_TARGETS[@]+"${EXPECTED_MAKE_INVOKED_TARGETS[@]}"} | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
mode_pin_only="$(LC_ALL=C comm -23 <(printf '%s\n' "$mode_pin_sorted") <(printf '%s\n' "$mode_obs_sorted"))"
mode_obs_only="$(LC_ALL=C comm -13 <(printf '%s\n' "$mode_pin_sorted") <(printf '%s\n' "$mode_obs_sorted"))"
mode_changed="$(printf '%s\n%s\n' "$mode_pin_only" "$mode_obs_only" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"

anchor_obs_sorted="$(printf '%s' "$anchorobserved" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
step_pin_sorted="$(printf '%s\n' ${EXPECTED_GATE_STEPS[@]+"${EXPECTED_GATE_STEPS[@]}"} | sed 's/|.*$//' | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
# A target already named by the mode block is NOT re-reported here. A mode change
# is the cause; "no step pinned" and "a step pinned that nothing uses" are its two
# symptoms, and telling the reader to add a step pin for a gate whose CI step was
# deleted is the wrong fix.
step_pin_only="$(LC_ALL=C comm -23 <(printf '%s\n' "$step_pin_sorted") <(printf '%s\n' "$anchor_obs_sorted") | LC_ALL=C comm -23 - <(printf '%s\n' "$mode_changed"))"
step_obs_only="$(LC_ALL=C comm -13 <(printf '%s\n' "$step_pin_sorted") <(printf '%s\n' "$anchor_obs_sorted") | LC_ALL=C comm -23 - <(printf '%s\n' "$mode_changed"))"

# ---- D. the two modes PARTITION the gated population, asserted not assumed ----
#
# The two set equalities above say WHICH members are in each array. What makes
# "exactly one of the two" a statement rather than a consequence of the if/else
# that classified them is this: the union of the observed modes must equal the
# gate-carrying non-excused population, which is accumulated before that branch,
# and the two must not intersect. With both equalities green this is implied, so it
# can only fire when the classification itself is wrong — which is exactly the
# reading no set pin can give you. This is equivalent to pinning the gate-step
# DOMAIN as {gate-carrying} − {excused} − {make-invoked}, spelled as two pins plus
# a partition instead of one pin plus a subtraction.
gated_sorted="$(printf '%s' "$gated" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
mode_union="$(printf '%s%s' "$makeobserved" "$anchorobserved" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
mode_overlap="$(LC_ALL=C comm -12 <(printf '%s\n' "$mode_obs_sorted") <(printf '%s\n' "$anchor_obs_sorted") | sed '/^[[:space:]]*$/d')"
partition_gap="$(LC_ALL=C comm -3 <(printf '%s\n' "$gated_sorted") <(printf '%s\n' "$mode_union") | sed '/^[[:space:]]*$/d')"
partition_bad=0
if [ -n "$mode_overlap" ] || [ -n "$partition_gap" ]; then
	partition_bad=1
	echo >&2
	echo "check-ci-reach: the two reach MODES do not partition the gate-carrying," >&2
	echo "      non-excused closure members. Every such member must be in exactly one of" >&2
	echo "      EXPECTED_MAKE_INVOKED_TARGETS or EXPECTED_GATE_STEPS, and the set pins above" >&2
	echo "      only fix WHICH members are in each — not that the two cover the population" >&2
	echo "      without overlapping. If one mode can absorb what the other drops, the" >&2
	echo "      cancellation those pins closed reappears in a new place." >&2
	if [ -n "$mode_overlap" ]; then
		echo >&2
		echo "  CLASSIFIED IN BOTH MODES:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$mode_overlap"
	fi
	if [ -n "$partition_gap" ]; then
		echo >&2
		echo "  CARRIES A GATE AND IS NOT EXCUSED, but landed in NEITHER mode (or in a mode" >&2
		echo "  without carrying a gate):" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$partition_gap"
	fi
	echo >&2
	echo "This is a defect in this script's own classification, not a repo state — fix" >&2
	echo "the mode branch in the main loop (#reversereachdirection)." >&2
	status=1
fi

if [ "$partition_bad" -eq 0 ] && [ -z "$mode_changed" ] && [ -z "$step_pin_only" ] && [ -z "$step_obs_only" ] && [ "$stepmiss_count" -eq 0 ]; then
	printf 'gate step pin satisfied: %s in their pinned CI step + %s invoked through `%s <target>` = %s gate(s), each population pinned as a SET in both directions\n' \
		"$step_ok" "$makereached_count" "$MAKE_BIN" \
		"$(printf '%s\n' "$gated_sorted" | awk 'NF { n++ } END { print n + 0 }')"
fi

# ---- C. The no-gate bucket, pinned by set equality --------------------------
#
# `no gate` is the one bucket this guard never requires CI to reach, which makes
# it the bucket to move a target INTO. Membership and the oracle both survive it:
# the name stays in `$ROOT_TARGET:`'s prerequisites and make really does run the
# neutered recipe. Measured -- `test:` with its body replaced by `true` -- the
# target silently moves from `reached` to `no gate` and the run exits 0.
#
# This one joins `status` instead of exiting, because by here the per-target
# report is already printed and is correct as far as it goes; the finding is a
# classification, not a reason to distrust the lines above.
nogate_sorted="$(printf '%s' "$nogate" | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
nogate_pin_sorted="$(printf '%s\n' ${EXPECTED_NO_GATE_TARGETS[@]+"${EXPECTED_NO_GATE_TARGETS[@]}"} | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)"
nogate_pin_only="$(LC_ALL=C comm -23 <(printf '%s\n' "$nogate_pin_sorted") <(printf '%s\n' "$nogate_sorted"))"
nogate_new="$(LC_ALL=C comm -13 <(printf '%s\n' "$nogate_pin_sorted") <(printf '%s\n' "$nogate_sorted"))"

# A guard that examined nothing must not report OK — the same vacuity rule the
# conformance guards apply (#lzvacuousrun).
if [ "$((reached + excused_ok + unreached_count + unreadable_count))" -eq 0 ]; then
	echo "check-ci-reach: '$ROOT_TARGET' has no prerequisite target carrying a gate — nothing was verified" >&2
	exit 1
fi

status=0

# An unreadable target is a REFUSAL, not a reach verdict: with the dry run broken
# this guard has no idea what the target runs, so it can neither require a CI step
# nor excuse the absence of one. Reported first, because every other line below is
# computed from dry runs and a broken one makes the rest of the report suspect.
if [ "$unreadable_count" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $unreadable_count target(s) whose dry run FAILED, so what they run is unknown:" >&2
	while IFS= read -r t; do
		[ -n "$t" ] || continue
		echo "  - $t   (\`$MAKE_BIN -n $t\` exited nonzero; its message is above)" >&2
	done <<<"$unreadable"
	echo >&2
	echo "This is not the same finding as 'CI does not reach it'. A target whose dry" >&2
	echo "run fails produces no commands, which this guard used to report as a target" >&2
	echo "'carrying no gate' and stop requiring in CI — a false green the root-level" >&2
	echo "\`$MAKE_BIN -n $ROOT_TARGET\` gate cannot see, because \$(MAKECMDGOALS) differs" >&2
	echo "between the root goal and this one (#lzgrepcpipefail). Fix the Makefile." >&2
	status=1
fi

if [ -n "$nogate_pin_only" ] || [ -n "$nogate_new" ]; then
	echo >&2
	echo "check-ci-reach: the set of closure targets carrying NO GATE does not match" >&2
	echo "      EXPECTED_NO_GATE_TARGETS in $0." >&2
	if [ -n "$nogate_new" ]; then
		echo >&2
		echo "  NOW carrying no gate, but NOT PINNED as such — a recipe stopped running" >&2
		echo "  anything this guard can require of CI, so the target quietly stopped being" >&2
		echo "  required to appear there while keeping its name and its place in the" >&2
		echo "  closure:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$nogate_new"
	fi
	if [ -n "$nogate_pin_only" ]; then
		echo >&2
		echo "  PINNED as carrying no gate, but it now carries one — the pin is stale:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$nogate_pin_only"
	fi
	echo >&2
	echo "Restore the recipe if it was neutered by accident; update" >&2
	echo "EXPECTED_NO_GATE_TARGETS in the same commit if the reclassification is" >&2
	echo "deliberate (#pinreachclosure)." >&2
	status=1
fi

# ---- D. findings, MODE FIRST (#reversereachdirection) ----------------------
#
# Order is load-bearing. A mode change is the CAUSE; a gate with no step pin and a
# step pin nothing uses are its SYMPTOMS, and reporting a symptom first sends the
# reader to the wrong fix -- "add a pin" for a gate whose CI step was deleted.
if [ -n "$mode_changed" ]; then
	echo >&2
	echo "check-ci-reach: the set of gates CI reaches through \`$MAKE_BIN <target>\` does not" >&2
	echo "      match EXPECTED_MAKE_INVOKED_TARGETS in $0." >&2
	if [ -n "$mode_obs_only" ]; then
		echo >&2
		echo "  CI NOW INVOKES THESE THROUGH MAKE, and they are not pinned that way — the" >&2
		echo "  gate stopped being spelled in CI, so it left the population whose step is" >&2
		echo "  pinned and entered the one whose step cannot be. Paired with deleting the" >&2
		echo "  target's EXPECTED_GATE_STEPS entry, this used to CANCEL and exit 0, leaving" >&2
		echo "  two integers on an OK line as the only trace:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$mode_obs_only"
	fi
	if [ -n "$mode_pin_only" ]; then
		echo >&2
		echo "  PINNED as make-invoked, but CI no longer invokes them that way — the step" >&2
		echo "  that ran \`$MAKE_BIN <target>\` was deleted or rewritten. Restoring that step" >&2
		echo "  is the fix; adding an EXPECTED_GATE_STEPS entry is not, unless CI now really" >&2
		echo "  spells the gate in a step of its own:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$mode_pin_only"
	fi
	echo >&2
	echo "A population pinned only as the complement of another pinned population is not" >&2
	echo "pinned against an edit that moves both together, and a COUNT IS NOT A PIN. Both" >&2
	echo "modes are therefore sets, compared in both directions. Retiring a gate from the" >&2
	echo "step-pinned population is meant to cost three reviewable changes that each name" >&2
	echo "it: the CI step body, the EXPECTED_GATE_STEPS deletion, and the addition to" >&2
	echo "EXPECTED_MAKE_INVOKED_TARGETS." >&2
	status=1
fi

if [ -n "$step_pin_only" ] || [ -n "$step_obs_only" ]; then
	echo >&2
	echo "check-ci-reach: the set of gates CI spells directly does not match the target" >&2
	echo "      set of EXPECTED_GATE_STEPS in $0." >&2
	if [ -n "$step_obs_only" ]; then
		echo >&2
		echo "  CI SPELLS THESE GATES, so there is a step to pin, and until it is pinned the" >&2
		echo "  gate is held only by the flat check — satisfied by any counted run: step" >&2
		echo "  anywhere in the workflow. Add \`target|step name\`:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$step_obs_only"
	fi
	if [ -n "$step_pin_only" ]; then
		echo >&2
		echo "  PINNED to a step, but not observed reaching CI by an anchor at all. The pin" >&2
		echo "  is asserting nothing: the target was excused, stopped carrying a gate, or" >&2
		echo "  left the closure. Drop the entry, or restore what the target used to run:" >&2
		while IFS= read -r t; do
			[ -n "$t" ] || continue
			echo "    - $t" >&2
		done <<<"$step_pin_only"
	fi
	status=1
fi

if [ "$stepmiss_count" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $stepmiss_count gate(s) whose anchors are somewhere in CI but NOT in the" >&2
	echo "      step EXPECTED_GATE_STEPS pins them to:" >&2
	while IFS= read -r t; do
		[ -n "$t" ] || continue
		echo "  - $t   (pinned step: \`$(gate_step_of "$t")\`; see the STEP-MISS lines above)" >&2
	done <<<"$stepmiss"
	echo >&2
	echo "This is NOT 'CI does not run the gate'. The flat check above passed, which" >&2
	echo "means some OTHER counted run: step spells what this target now runs. That is" >&2
	echo "the recipe repoint: the target keeps its name, its place in the closure and its" >&2
	echo "bucket, \`$MAKE_BIN -n $ROOT_TARGET\` really runs the new command, and the gate the" >&2
	echo "step was added for runs zero times from \`$MAKE_BIN $ROOT_TARGET\`. Pre-fix that" >&2
	echo "state printed a \`cmp\`-identical healthy report at exit 0." >&2
	echo >&2
	echo "Two remedies, NOT interchangeable: restore the recipe if it was repointed by" >&2
	echo "accident — that is the finding — or, if the gate genuinely moved to another" >&2
	echo "step, re-point its EXPECTED_GATE_STEPS entry in the same commit." >&2
	status=1
fi

if [ "$stale_count" -gt 0 ]; then
	echo >&2
	while IFS= read -r t; do
		[ -n "$t" ] || continue
		echo "check-ci-reach: '$t' is excused in $CONF but CI DOES reach it — remove the excuse" >&2
	done <<<"$stale"
	status=1
fi

if [ "$unreached_count" -gt 0 ]; then
	echo >&2
	echo "check-ci-reach: $unreached_count target(s) run by 'make $ROOT_TARGET' that no CI run: step reaches:" >&2
	while IFS= read -r t; do
		[ -n "$t" ] || continue
		echo "  - $t" >&2
	done <<<"$unreached"
	echo >&2
	echo "Add a CI step that runs it, or add an excuse with a reason to $CONF." >&2
	status=1
fi

if [ "$status" -eq 0 ]; then
	echo "check-ci-reach: OK — $reached target(s) reached by CI, $excused_ok excused, $nogate_count carrying no gate"
fi
exit "$status"
