#!/usr/bin/env bash
# Conformance-coverage guard (#portconformancecoverage).
#
# Fails the build when the canonical corpus in ../lazily-spec/conformance/ grows a
# fixture that no test in this repo even mentions. That is the drift this guard
# exists for: a fixture lands upstream, every binding stays green, and nobody
# learns that one of them is not replaying it.
#
# This binding uses the RUNTIME manifest (#lazilyupgradeconformance), not the
# static grep it started with. The test run records every file it actually reads
# from the conformance corpus, so a fixture named in a comment but hand-transcribed
# — the drift found in lazily-cpp's queue tests — is caught here. A source grep
# cannot see that case at all.
#
# A missing manifest is missing EVIDENCE and fails. It does not mean "no fixtures
# were read"; it means the suite ran without the recorder attached, and passing in
# that state is the vacuous green this guard exists to prevent.
set -euo pipefail

# ---- RUNG: only the seam may SPELL the corpus root (#lzcorpusrootguards) ----
#
# `LAZILY_SPEC_CONFORMANCE_DIR` is what makes a conformance replay falsifiable:
# copy the corpus, flip one assertion, confirm the suite reddens. A runner that
# builds its own "../lazily-spec/conformance/<area>" instead of asking
# SpecCorpus never sees the override — and the failure is SILENT, because a
# runner reading the DEFAULT corpus while believing it was redirected is green
# either way. Nothing else in this file can see it: the manifest legs below
# audit fixtures that WERE opened, and a hardcoded root opens real fixtures.
#
# The measurement is what makes this worth a guard rather than a convention.
# Truncating fixtures in a scratch corpus reddened ZERO lazily-zig tests before
# its 14 hardcoded roots were removed and 26 after; lazily-rs reached 0 of 25
# areas. lazily-cs measured CLEAN — 25/25 areas reddened, because
# `SpecCorpus.Locate()` reads the env var first and fails closed — so this rung
# is regression PREVENTION. Nothing but convention stops a new runner from
# spelling the path itself, and that runner would be invisible.
#
# It runs FIRST, before the corpus is even located: it is a source-hygiene
# check with no dependency on a sibling checkout, and the absent-corpus skip
# further down must not be able to swallow it.
#
# `SpecCorpus.cs` is the one legitimate mention — it DECLARES the default as
# `SiblingRelativePath`. The ~19 files that name `SpecCorpus.SiblingRelativePath`
# as a SYMBOL inside assert messages are not literals and never trip this.
CORPUS_ROOT_ALLOW="tests/Lazily.Tests/SpecCorpus.cs"
read -r -a CORPUS_ROOT_SCAN_DIRS <<< "${LAZILY_CORPUS_ROOT_SCAN_DIRS:-src tests benchmarks}"

# The floor exists because every clause above reasons about files the walk
# FOUND, so all of it is vacuously satisfied by an empty file list: a scan that
# examined nothing reports no offenders and would print OK. That is the vacuous
# green the rest of this file refuses (#lzvacuousrun). Pinned below the real
# tree (140 sources) with headroom; a drop this far means the walk is pointed
# somewhere wrong, not that the repo shrank.
MIN_SCANNED_SOURCES="${MIN_SCANNED_SOURCES:-100}"

collect_sources() {
  for d in "${CORPUS_ROOT_SCAN_DIRS[@]}"; do
    [ -d "$d" ] || continue
    find "$d" -type f -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*'
  done | sort
}

CORPUS_ROOT_PY="$(cat <<'PY'
import re
import sys

NEEDLE = "../lazily-spec/conformance"
NEEDLE_SQUASHED = re.sub(r"[\\/\s]", "", NEEDLE)
WINDOW_LITERALS = 12
WINDOW_LINES = 10


def _read_raw(text, i, line):
    n = len(text)
    q = 0
    while i + q < n and text[i + q] == '"':
        q += 1
    i += q
    start = i
    while i < n:
        if text[i] == '"':
            run = 0
            while i + run < n and text[i + run] == '"':
                run += 1
            if run >= q:
                body = text[start:i]
                return i + run, body, line + body.count("\n")
            i += run
            continue
        i += 1
    body = text[start:]
    return n, body, line + body.count("\n")


def _read_verbatim(text, i, line):
    n = len(text)
    i += 1
    out = []
    while i < n:
        c = text[i]
        if c == '"':
            if i + 1 < n and text[i + 1] == '"':
                out.append('"')
                i += 2
                continue
            return i + 1, "".join(out), line
        if c == "\n":
            line += 1
        out.append(c)
        i += 1
    return n, "".join(out), line


def _read_regular(text, i, line, interp):
    n = len(text)
    i += 1
    out = []
    while i < n:
        c = text[i]
        if c == "\\" and i + 1 < n:
            nxt = text[i + 1]
            out.append("\\" if nxt == "\\" else nxt)
            i += 2
            continue
        if c == '"':
            return i + 1, "".join(out), line
        if interp and c == "{":
            if i + 1 < n and text[i + 1] == "{":
                i += 2
                continue
            # Skip the interpolation hole, including any nested string literal,
            # so a quote inside `{x ?? "y"}` cannot desync the scan.
            depth = 1
            i += 1
            while i < n and depth > 0:
                d = text[i]
                if d == "{":
                    depth += 1
                elif d == "}":
                    depth -= 1
                elif d == '"':
                    i, _, line = _read_regular(text, i, line, False)
                    continue
                elif d == "\n":
                    line += 1
                i += 1
            continue
        if c == "\n":
            # Unterminated in valid C#; tolerate rather than desync.
            line += 1
        out.append(c)
        i += 1
    return n, "".join(out), line


def literals(text):
    """Every string-literal VALUE in source order, with its opening line.

    Comments are skipped: a file may legitimately quote the corpus root while
    explaining it, and lint-forcing an explanation to stop describing the thing
    it explains trades real documentation for a guard that is easy to satisfy.
    """
    out = []
    i = 0
    n = len(text)
    line = 1
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] == "\n":
                    line += 1
                i += 1
            i += 2
            continue
        if c == "'":
            i += 1
            while i < n and text[i] != "'":
                if text[i] == "\\":
                    i += 1
                if i < n and text[i] == "\n":
                    line += 1
                i += 1
            i += 1
            continue
        if c in "@$":
            j = i
            verbatim = False
            interp = False
            while j < n and text[j] in "@$":
                if text[j] == "@":
                    verbatim = True
                else:
                    interp = True
                j += 1
            if j < n and text[j] == '"':
                start = line
                if not verbatim and text[j:j + 3] == '"""':
                    i, val, line = _read_raw(text, j, line)
                elif verbatim:
                    i, val, line = _read_verbatim(text, j, line)
                else:
                    i, val, line = _read_regular(text, j, line, interp)
                out.append((start, val))
                continue
            i = j
            continue
        if c == '"':
            start = line
            if text[i:i + 3] == '"""':
                i, val, line = _read_raw(text, i, line)
            else:
                i, val, line = _read_regular(text, i, line, False)
            out.append((start, val))
            continue
        i += 1
    return out


def squash(s):
    return re.sub(r"[\\/\s]", "", s)


def offenders(text):
    found = []
    lits = literals(text)
    flagged = set()
    # Pass 1 — the single-literal form.
    for idx, (line, val) in enumerate(lits):
        if NEEDLE in val.replace("\\", "/"):
            found.append((line, "single literal", val))
            flagged.add(idx)
    # Pass 2 — the JOINED-SEGMENT form: Path.Combine("..", "lazily-spec",
    # "conformance", area) or "../lazily-spec" + "/conformance". No single piece
    # carries the root, so the match runs over a short run of ADJACENT literals
    # with the separators squashed out. This is the form the lazily-go and
    # lazily-js guards missed and were proven evadable on.
    for idx, (line, val) in enumerate(lits):
        if idx in flagged:
            continue
        joined = squash(val)
        for j in range(idx + 1, min(idx + WINDOW_LITERALS, len(lits))):
            if j in flagged:
                break
            nline, nval = lits[j]
            if nline - line > WINDOW_LINES:
                break
            joined += squash(nval)
            if NEEDLE_SQUASHED in joined:
                found.append((line, "joined segments", " + ".join(
                    repr(v) for _, v in lits[idx:j + 1])))
                flagged.update(range(idx, j + 1))
                break
    found.sort()
    return found


def main(argv):
    allow = set(argv[1].split(",")) if argv[1] else set()
    paths = [p for p in sys.stdin.read().split("\n") if p]
    examined = 0
    hits = []
    for p in paths:
        rel = p[2:] if p.startswith("./") else p
        if rel in allow:
            continue
        try:
            with open(p, "r", encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except OSError as exc:
            print("ERROR: cannot read %s: %s" % (p, exc), file=sys.stderr)
            return 2
        examined += 1
        for line, form, detail in offenders(text):
            hits.append((rel, line, form, detail))
    print("EXAMINED %d" % examined)
    for rel, line, form, detail in hits:
        print("HIT %s:%d\t%s\t%s" % (rel, line, form, detail))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
PY
)"

corpus_root_report="$(collect_sources | python3 -c "$CORPUS_ROOT_PY" "$CORPUS_ROOT_ALLOW")"
scanned="$(sed -n 's/^EXAMINED //p' <<< "$corpus_root_report")"

if [ -z "$scanned" ]; then
  echo "ERROR: the corpus-root scanner produced no verdict at all." >&2
  echo "       That is missing EVIDENCE, not a clean tree." >&2
  exit 1
fi
if [ "$scanned" -lt "$MIN_SCANNED_SOURCES" ]; then
  echo "ERROR: corpus-root scan examined only $scanned C# sources, expected >= $MIN_SCANNED_SOURCES." >&2
  echo "       Searched: ${CORPUS_ROOT_SCAN_DIRS[*]} (from \$PWD=$PWD)." >&2
  echo "       Reporting OK here would be a pass over nothing: no files means no" >&2
  echo "       offenders, which is not the same finding as no offenders in the" >&2
  echo "       tree (#lzvacuousrun)." >&2
  exit 1
fi
if grep -q '^HIT ' <<< "$corpus_root_report"; then
  echo "ERROR: these sources SPELL the canonical corpus root instead of resolving it" >&2
  echo "       through SpecCorpus:" >&2
  grep '^HIT ' <<< "$corpus_root_report" | sed 's/^HIT /         /' >&2
  echo "       LAZILY_SPEC_CONFORMANCE_DIR does not reach a hardcoded path, so those" >&2
  echo "       fixtures are replayed unfalsifiably — a perturbation probe cannot" >&2
  echo "       redden them and the runner looks green either way. Resolve the corpus" >&2
  echo "       with SpecCorpus.Root / SpecCorpus.Load (#lzcorpusrootguards)." >&2
  exit 1
fi

echo "corpus-root guard OK: $scanned C# sources examined, none spell '../lazily-spec/conformance'" \
     "(single-literal AND joined-segment forms; comments skipped; 1 allowlisted seam)"


SPEC_DIR="${LAZILY_SPEC_CONFORMANCE_DIR:-../lazily-spec/conformance}"

# An EXPLICIT override that names no directory is a wrong invocation, never an
# absent checkout, so it fails before the context-split skip below can see it
# (#lzoverrideallrunners). The skip exists for a laptop with no sibling clone —
# the operator asked for nothing in particular and got nothing. Someone who set
# LAZILY_SPEC_CONFORMANCE_DIR asked for a SPECIFIC corpus, and exiting 0 there
# reports "conformance OK" about a corpus that was never read: exactly the
# perturbation probe this var exists for (copy the corpus, flip one assertion,
# confirm the suite reddens) silently reporting green from a typo in the path.
# `SpecCorpus.Locate()` in the test host already throws on this; the guard that
# audits the same run must not be the softer of the two.
if [ -n "${LAZILY_SPEC_CONFORMANCE_DIR:-}" ] && [ ! -d "$LAZILY_SPEC_CONFORMANCE_DIR" ]; then
  echo "FAIL: LAZILY_SPEC_CONFORMANCE_DIR=$LAZILY_SPEC_CONFORMANCE_DIR names no directory." >&2
  echo "      An explicit override is a request for one specific corpus. Skipping" >&2
  echo "      here would report conformance OK having read nothing from it, and" >&2
  echo "      falling back to the sibling would audit a corpus nobody asked for." >&2
  echo "      Fix the path; do not unset the variable to make this pass." >&2
  exit 1
fi

if [ ! -d "$SPEC_DIR" ]; then
  # The one remaining path out of this script that is not a measurement. It exists
  # for a working copy with no sibling checkout, where the alternative is a guard
  # that cannot be run at all. It is NOT acceptable where the corpus is supposed to
  # be there: a clone step that silently no-ops would turn every rung below into a
  # green that proves nothing (#lzguardsnotinci).
  #
  # The skip is split by CONTEXT, not by opt-in (#lzvacuousrun). This repo's own CI
  # sets LAZILY_CONFORMANCE_STRICT, but a flag only this workflow remembers to set
  # is not a guard — a new job, a reusable workflow, or a fork that forgets it gets
  # the local behaviour and reports conformance OK having examined zero fixtures.
  # `CI` is set by every mainstream runner and by nothing on a laptop, so an absent
  # corpus under CI is read as missing EVIDENCE (the checkout is wrong) rather than
  # evidence of absence. LAZILY_CONFORMANCE_STRICT is kept as the explicit override
  # for environments that assert presence without setting CI.
  if [ -n "${CI:-}" ]; then
    echo "ERROR: canonical corpus not found at $SPEC_DIR, and CI is set." >&2
    echo "       Under CI this is missing EVIDENCE, not evidence of absence: the" >&2
    echo "       checkout is wrong, not the corpus. Exiting 0 here would report" >&2
    echo "       conformance OK having examined zero fixtures (#lzvacuousrun)." >&2
    exit 1
  fi
  if [ -n "${LAZILY_CONFORMANCE_STRICT:-}" ]; then
    echo "FAIL: canonical corpus not found at $SPEC_DIR, and LAZILY_CONFORMANCE_STRICT is set." >&2
    echo "      Skipping here would report OK while checking nothing. The corpus is" >&2
    echo "      expected to be present in this environment — fix the checkout, do not" >&2
    echo "      unset the flag." >&2
    exit 1
  fi
  echo "SKIP: canonical corpus not found at $SPEC_DIR (clone the lazily-spec sibling)" >&2
  echo "      Local checkout only — this is a hard failure under CI." >&2
  exit 0
fi

# Fixtures deliberately not covered by this binding yet. Every entry is a
# reviewed finding, never a silent skip.
KNOWN_UNCOVERED=(
  # Reactive egress is currently Rust-only; C# has no egress replay runner.
  "egress/egress_generation_fence.json"
  "egress/egress_inflight_window.json"
  "egress/egress_ordered_ack.json"
  "egress/egress_retry_budget.json"
  # Experimental protobuf-v1 generation is piloted in Rust/Kotlin/TypeScript;
  # C# must negotiate the capability before replaying this typed trace.
  "protobuf/graph_boundary_traces.json"
)

# Minimum DISTINCT canonical fixtures this binding must be observed OPENING.
#
# The per-fixture check below is exact in one direction only: it fails on a
# fixture the corpus HAS that the suite did not open. It cannot fail on a suite
# that opened almost nothing because the corpus itself came up short — that path
# reports "coverage OK: 2/2" and exits 0, which is the vacuous green #lzvacuousrun
# named after this binding printed "0/0" on three rungs in a row.
#
# EXACT: 145 is what a green CI run on the current corpus actually opens, with
# no margin. NEVER lower this to make the gate green: a drop means a replay was
# removed, renamed, or short-circuited, and that is the finding, not the floor.
#
# When you add replays, set this to the number the gate REPORTS afterwards — do
# not tally "the N I just added" onto the old value (#lzscenariofloordrift).
# That accounting, which the arithmetic this comment replaced used to spell out
# fixture by fixture, adds your delta on top of a floor that already sat under
# reality, so the gap only ever widens. It had reached 141 against an actual
# 145: four fixtures could stop being opened while the gate kept printing OK.
#
# Pinned from CI run 31347367370 (`conformance coverage OK: 145/150`), which
# matches a local green `make check`. Verified exact: 146 fails this floor.
MIN_FIXTURES="${MIN_FIXTURES:-145}"

# Scenarios deliberately not replayed, one per line as
#   corpus/fixture.json|scenario-id|reason
#
# The scenario twin of KNOWN_UNCOVERED, kept HERE so there is one place to read
# what this binding does not prove (#lzscenariocoverage). A fixture with several
# named scenarios can be PARTIALLY replayed and neither guard above notices: the
# coverage check asks only whether the FILE was opened, and one scenario is enough
# to answer yes.
#
# Two-directional, exactly as KNOWN_UNCOVERED is. An entry for a scenario this run
# DID replay is stale and fails; an entry naming an id the fixture does not carry
# is stale and fails. Prefer implementing the scenario — a known-skipped scenario is
# the work this guard exists to force, and the reason has to say what this binding
# cannot express, not that nobody got to it.
KNOWN_UNREPLAYED_SCENARIOS=(
)

# Minimum DISTINCT scenarios this binding must be observed REPLAYING — the
# scenario twin of MIN_FIXTURES, and the floor whose absence was #lzvacuousrun's
# last live hole in this repo.
#
# The scenario rung walks the scenarios that OPENED fixtures carry, so it is
# vacuously green over an empty population in a way MIN_FIXTURES cannot see: a
# corpus of 138 fixtures that carry no `scenarios` array at all satisfies rung 1
# at 138/138 and then prints "0/0 scenarios across 0 opened fixtures" and exits 0.
# That was reproducible on this script before this constant existed.
#
# EXACT: 165 is what a green CI run on the current corpus actually replays,
# with no margin. A fixture growing scenarios while the floor stays put is how
# a binding replays the old half of a hardened fixture and reports the same
# green as before, so the floor moves WITH the corpus — all the way, not part
# of the way.
#
# When you add replays, set this to the number the gate REPORTS afterwards — do
# not tally "the N I just added" onto the old value, and do not leave it
# "slightly below the real number" for churn headroom (#lzscenariofloordrift).
# Both habits, which the arithmetic this comment replaced practiced, leave slack
# on top of slack: this had reached 148 against an actual 165, so SEVENTEEN
# scenarios could silently stop being reached while the gate kept printing OK.
# Corpus churn tripping the floor is the guard working; that is when you re-pin
# it to the new reported number.
#
# NEVER lower this to make the gate green: a drop means scenarios stopped being
# reached, and that is the finding, not the floor.
#
# Pinned from CI run 31347367370 (`scenario replay coverage OK: 165/165`), which
# matches a local green `make check`. Verified exact: 166 fails this floor.
MIN_SCENARIOS="${MIN_SCENARIOS:-165}"

MANIFEST="${LAZILY_CONFORMANCE_MANIFEST:-build/conformance-fixtures-loaded.txt}"
if [ ! -s "$MANIFEST" ]; then
  echo "FAIL: no conformance manifest at $MANIFEST." >&2
  echo "      Run the suite with LAZILY_CONFORMANCE_MANIFEST set so the recorder" >&2
  echo "      attaches. An absent manifest is missing evidence, not evidence of" >&2
  echo "      absence." >&2
  exit 1
fi
OPENED="$(sort -u "$MANIFEST")"

missing=0
total=0
covered=0
while IFS= read -r fixture; do
  total=$((total + 1))
  name="$(basename "$fixture")"
  # Here-string, NOT a pipe. With `set -o pipefail`, `printf ... | grep -q` reports
  # FAILURE when grep matches: grep -q exits immediately on the first hit, printf
  # takes SIGPIPE writing the rest, and pipefail surfaces printf's death as the
  # pipeline's status. The check then inverts — every covered fixture is reported
  # missing. That is exactly how it behaved before this line changed.
  if grep -qxF "$fixture" <<< "$OPENED"; then
    covered=$((covered + 1))
    continue
  fi
  excused=0
  for known in "${KNOWN_UNCOVERED[@]}"; do
    if [ "$known" = "$fixture" ]; then excused=1; break; fi
  done
  if [ "$excused" -eq 0 ]; then
    echo "ERROR: canonical fixture '$fixture' was NOT opened by the suite." >&2
    echo "       A runner may still name it in source while no longer reading it —" >&2
    echo "       that is the drift this manifest exists to catch. Replay it, or add" >&2
    echo "       it to KNOWN_UNCOVERED with a reason." >&2
    missing=$((missing + 1))
  fi
done < <(cd "$SPEC_DIR" && find . -name '*.json' | sed 's|^\./||' | sort)

# A stale allowlist is its own drift, in TWO directions (#lzcovallowlistrot).
#
#   1. The entry names a fixture that no longer exists — the corpus moved and
#      nobody updated the excuse.
#   2. The entry names a fixture the suite DOES open — the excuse outlived the
#      gap it documented. Nothing above can see this: a covered fixture takes
#      the `continue` branch and never consults KNOWN_UNCOVERED at all, so a
#      stale excuse sits there forever understating what this binding replays.
#      That is the same understating rot #lzcoverageaudit corrected one layer up
#      in lazily-spec's coverage.json, and the ledger-rot direction that does not
#      announce itself: the build stays green while the count reads low.
#
# The covered-check comparison is reused EXACTLY — `grep -qxF` against the same
# $OPENED set, on the corpus-relative path. A looser match here (basename, or a
# substring) would fire on a fixture the suite never touched.
for known in "${KNOWN_UNCOVERED[@]}"; do
  if [ ! -f "$SPEC_DIR/$known" ]; then
    echo "ERROR: KNOWN_UNCOVERED lists '$known', which is not in the canonical corpus." >&2
    missing=$((missing + 1))
    continue
  fi
  if grep -qxF "$known" <<< "$OPENED"; then
    echo "ERROR: KNOWN_UNCOVERED lists '$known', but the suite OPENED it." >&2
    echo "       The excuse is stale: this binding replays that fixture now." >&2
    echo "       Delete the entry. An allowlist that outlives its gap understates" >&2
    echo "       coverage silently — it can never turn the build red on its own." >&2
    missing=$((missing + 1))
  fi
done

# ---- The evidence channel guards itself ----
#
# Everything above asks "did the suite open fixture X?" and answers it out of the
# manifest. Nothing yet asks whether the manifest is describing THIS corpus. A
# recorded id that names no file under $SPEC_DIR means the evidence file was
# truncated, interleaved by concurrent test hosts appending at process exit, or
# carried over from a run against a different corpus — and coverage computed from
# it cannot be trusted in EITHER direction: the count is inflated by ids nobody
# can resolve, while a real fixture the suite stopped opening can hide behind
# them. This is the fixture twin of the ledger self-check the scenario leg below
# already makes (an id the fixture does not carry), and lazily-rs has carried it
# on this rung since the manifest replaced the static grep.
#
# Scenario records ride in the same file, distinguished by a TAB (see
# SpecCorpus.RecordScenario). They name an id inside a fixture, not a path, so
# they are skipped here and checked against the fixture's own scenarios by the
# python leg further down.
while IFS= read -r id; do
  [ -n "$id" ] || continue
  case "$id" in *"$(printf '\t')"*) continue ;; esac
  if [ ! -f "$SPEC_DIR/$id" ]; then
    echo "ERROR: manifest records '$id', which names no file in $SPEC_DIR." >&2
    echo "       The recorder is dropping or interleaving writes, or this evidence" >&2
    echo "       file was written against a different corpus; coverage computed from" >&2
    echo "       this manifest cannot be trusted." >&2
    missing=$((missing + 1))
  fi
done <<< "$OPENED"

# ---- Positive-evidence floor (#lzvacuousrun) ----
# Every check above reasons about fixtures the corpus LISTED and the run OPENED,
# so all of it is vacuously satisfied by an empty population: zero fixtures means
# zero uncovered fixtures and zero stale excuses. The loop cannot tell "nothing is
# wrong" from "nothing was examined", so the magnitude is asserted explicitly
# before anything reports OK.
#
# MIN_FIXTURES below happens to catch an empty corpus too, but only as a side
# effect of the floor being large; it would stop catching it the moment anyone
# passed MIN_FIXTURES=0, and its message ("a replay was removed") would misname
# the fault. An empty CORPUS is a different finding from a short SUITE and gets
# its own check.
if [ "$total" -eq 0 ]; then
  echo "ERROR: the corpus at $SPEC_DIR listed ZERO fixtures." >&2
  echo "       Every check above is vacuously green over an empty population —" >&2
  echo "       zero fixtures cannot produce an uncovered one (#lzvacuousrun)." >&2
  echo "       The checkout is wrong, or LAZILY_SPEC_CONFORMANCE_DIR points at the" >&2
  echo "       wrong directory. Do not treat this as coverage." >&2
  missing=$((missing + 1))
fi

if [ "$covered" -lt "$MIN_FIXTURES" ]; then
  echo "ERROR: only $covered distinct canonical fixtures were OPENED, expected >= $MIN_FIXTURES." >&2
  echo "       A replay was removed, renamed, or short-circuited — or the corpus checkout is" >&2
  echo "       short. Do not lower MIN_FIXTURES to fix this." >&2
  missing=$((missing + 1))
fi

if [ "$missing" -gt 0 ]; then
  echo "conformance coverage FAILED: $missing problem(s)" >&2
  exit 1
fi

echo "conformance coverage OK: $covered/$total canonical fixtures OPENED by the suite" \
     "(${#KNOWN_UNCOVERED[@]} listed as known-uncovered; floor $MIN_FIXTURES;" \
     "runtime manifest — these bytes were really read)"

# -- RUNG 0: the assertion-block BIND ledger (#lznullformblind) ---------------
#
# Every rung above and below is scoped to a block a runner already BOUND to a
# FixtureAssertions tracker. The unconsumed-key check fires on a key nothing
# read; the read-but-not-asserted check on a key read and discarded; the prose
# ledger on a discharge naming nothing. NONE of them can fire for a block no
# runner ever bound, because there is no tracker: its keys are not unread,
# nothing reads them, and the fixture reports exactly nothing. lazily-dart found
# two such blocks carrying eight silent keys, lazily-cpp a third, lazily-zig
# twenty-two.
#
# `SpecCorpus.Load` inventories every `assertions` block at read time and the
# `FixtureAssertions` constructor books one as BOUND, both riding this same
# manifest under `blocks-` prefixes. The two sides are matched by CONTENT digest
# and never by the block's label: runners spell those inconsistently, and a
# label-keyed ledger would silently miss the mismatch rather than report it.
#
# An unbindable block belongs HERE, as a documented excuse read on every run, not
# as a runner fabricated to manufacture coverage. Format: "fixture|where|reason".
KNOWN_UNBOUND_BLOCKS=(
)

BLOCK_GUARD_PY="$(cat <<'PY'
import os
import sys

manifest_path = sys.argv[1]

excuses = {}
for raw in sys.argv[2:]:
    raw = raw.strip()
    if not raw:
        continue
    parts = raw.split("|", 2)
    if len(parts) != 3 or not parts[2].strip():
        print(
            f"ERROR: malformed KNOWN_UNBOUND_BLOCKS entry {raw!r} — expected "
            "fixture|where|reason, with a non-empty reason. An excuse with no reason "
            "is an unexplained gap wearing a green badge.",
            file=sys.stderr,
        )
        sys.exit(1)
    excuses[f"{parts[0]}|{parts[1]}"] = parts[2].strip()

declared = {}
bound = set()
with open(manifest_path, encoding="utf-8") as handle:
    for line in handle:
        parts = line.rstrip("\n").split("\t")
        if parts[0] == "blocks-declared" and len(parts) == 4:
            declared.setdefault(parts[2], set()).add(f"{parts[1]}|{parts[3]}")
        elif parts[0] == "blocks-bound" and len(parts) == 2:
            bound.add(parts[1])

unbound = []
for digest, sites in sorted(declared.items()):
    if digest in bound:
        continue
    unbound.extend(site for site in sorted(sites) if site not in excuses)

if unbound:
    print(
        f"ERROR: {len(unbound)} assertion block(s) were carried by an OPENED fixture and\n"
        "       bound by no runner. Every other rung is scoped to blocks a runner bound,\n"
        "       so these report nothing at all rather than reporting a gap:",
        file=sys.stderr,
    )
    for site in unbound:
        print(f"         {site}", file=sys.stderr)
    print(
        "       Bind each with FixtureAssertions.Of/Wrap and assert its keys, or add it\n"
        "       to KNOWN_UNBOUND_BLOCKS with a reason so the gap is visible every run\n"
        "       instead of invisible.",
        file=sys.stderr,
    )
    sys.exit(1)

# Positive-evidence floor (#lzvacuousrun): zero declared blocks means zero
# unbound blocks, which reports OK having compared nothing.
#
# EXACT: 33 is what a green CI run on the current corpus actually inventories,
# with no margin. Set this to the number the guard REPORTS after adding
# replays; do not add "the N I just added" to the old value and do not leave
# headroom for churn (#lzscenariofloordrift). It had reached 25 against an
# actual 33, so eight blocks could stop being inventoried while this printed OK.
#
# Pinned from CI run 31347367370 (`assertion-block bind OK: 33/33`), which
# matches a local green `make check`. Verified exact: 34 fails this floor.
min_blocks = int(os.environ.get("MIN_BLOCKS", "33"))
if len(declared) < min_blocks:
    print(
        f"ERROR: only {len(declared)} distinct assertion blocks were inventoried, expected "
        f">= {min_blocks}.\n"
        "       The loader-side inventory detached, or fixtures stopped being read.\n"
        "       Do not lower MIN_BLOCKS to fix this.",
        file=sys.stderr,
    )
    sys.exit(1)

print(
    f"assertion-block bind OK: {len(declared)}/{len(declared)} assertion blocks carried by "
    f"opened fixtures were BOUND to a tracker ({len(excuses)} declared unbindable; floor "
    f"{min_blocks}; content-keyed, so a runner's block NAME cannot satisfy it)"
)
PY
)"

python3 -c "$BLOCK_GUARD_PY" "$MANIFEST" \
  ${KNOWN_UNBOUND_BLOCKS[@]+"${KNOWN_UNBOUND_BLOCKS[@]}"}

# -- Per-scenario replay accounting (#lzscenariocoverage) ---------------------
#
# The rung below the fixture check above. The manifest carries two kinds of line:
# a bare `corpus/fixture.json` means the file was OPENED, a `corpus/fixture.json`
# + TAB + id line means that SCENARIO was reached at the point of replay. The
# scenario ids on disk are read HERE, independently of the runner, so a runner
# that resolves an id wrongly shows up as a mismatch rather than agreeing with
# itself.
#
# Id resolution is the fixed order every binding uses: `id`, else
# `name`. There is no positional fallback (#lzspecscenarioids): an id derived from
# a POSITION silently rebinds to a different scenario when the corpus array is
# reordered, so an unidentified scenario is an error here rather than an invented
# id.
command -v python3 >/dev/null 2>&1 || {
  echo "FAIL: python3 is required to read scenario ids out of the corpus." >&2
  echo "      Without it the scenario ledger cannot be verified, and passing in" >&2
  echo "      that state is missing evidence, not evidence of absence." >&2
  exit 1
}

# The excuse list is passed as ARGV, not on stdin. `python3 - <<EOF` reads the
# PROGRAM from stdin, so a pipe into it is swallowed by the heredoc and every
# excuse silently vanishes — the guard then reports OK with a stale excuse sitting
# right there in the array. That is exactly the vacuous green everything here is
# built to prevent, and it is how this block was written first.
SCENARIO_GUARD_PY="$(cat <<'PY'
import json
import os
import sys

spec_dir, manifest_path = sys.argv[1], sys.argv[2]

excuses = {}
excuse_errors = []
for line in sys.argv[3:]:
    line = line.strip()
    if not line:
        continue
    parts = line.split("|", 2)
    if len(parts) != 3 or not parts[2].strip():
        excuse_errors.append(
            f"ERROR: malformed KNOWN_UNREPLAYED_SCENARIOS entry {line!r} — expected "
            "corpus/fixture.json|scenario-id|reason, with a non-empty reason. An "
            "excuse nobody had to justify is an allowlist entry wearing a different hat."
        )
        continue
    excuses[(parts[0], parts[1])] = parts[2].strip()

PROSE_VERIFIED = "prose-verified"
# The rung-0 channels ride the same manifest under their own prefixes
# (#lznullformblind). A corpus-relative fixture id can never be spelled like one,
# so the split stays unambiguous — but a `blocks-bound<TAB><digest>` line read by
# the branch below would be reported as a scenario of a fixture named
# "blocks-bound".
BLOCK_MARKERS = ("blocks-declared", "blocks-bound")

opened = set()
replayed = set()
prose_verified = set()
with open(manifest_path, encoding="utf-8") as handle:
    for line in handle:
        line = line.rstrip("\n")
        if not line:
            continue
        if "\t" in line:
            head, tail = line.split("\t", 1)
            # The prose marker is a PREFIX (see SpecCorpus.RecordProseVerified): a
            # `fixture<TAB>id` line already means "this scenario was replayed", so a
            # trailing marker would be read below as an id the fixture does not carry.
            if head == PROSE_VERIFIED:
                prose_verified.add(tail)
            elif head in BLOCK_MARKERS:
                continue
            else:
                replayed.add((head, tail))
        else:
            opened.add(line)


def ids_of(path):
    """`id`, else `name` — the fixed resolution order, with no third step.

    The positional `#<n>` fallback is gone (#lzspecscenarioids). It let the ledger
    identify a scenario BY POSITION, which silently rebinds to a different scenario
    when the corpus array is reordered -- the ledger says "index 1 was replayed",
    this reader looks at whatever now sits at index 1, and the two agree with each
    other about the wrong thing. An unidentified scenario is reported, never given
    an invented id. A blank identifier is refused too: it would file every blank-id
    scenario under a single ledger entry.
    """
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    scenarios = document.get("scenarios") if isinstance(document, dict) else None
    if not isinstance(scenarios, list):
        return []
    resolved = []
    for index, scenario in enumerate(scenarios):
        identifier = None
        if isinstance(scenario, dict):
            for key in ("id", "name"):
                value = scenario.get(key)
                if isinstance(value, str) and value.strip():
                    identifier = value
                    break
        resolved.append((identifier if identifier is not None else f"#{index}", identifier is None))
    return resolved


def declares_prose(path):
    """Does this fixture's `assertions` block declare prose keys (#lzprosekeyconvention)?

    Read from the CORPUS, never from a list kept here: rule 8's required set of
    verifications is derived, so a fixture that grows an `assertions.prose` array
    upstream starts demanding a discharge here without anyone updating a count.
    """
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    if not isinstance(document, dict):
        return False
    assertions = document.get("assertions")
    if not isinstance(assertions, dict):
        return False
    return isinstance(assertions.get("prose"), list) and bool(assertions["prose"])


on_disk = {}
declaring = set()
for root, _, files in os.walk(spec_dir):
    for name in sorted(files):
        if not name.endswith(".json"):
            continue
        full = os.path.join(root, name)
        key = os.path.relpath(full, spec_dir)
        found = ids_of(full)
        if found:
            on_disk[key] = found
        if declares_prose(full):
            declaring.add(key)

errors = list(excuse_errors)
unidentified = []
fixtures_checked = 0
scenarios_total = 0
scenarios_replayed = 0

for fixture in sorted(on_disk):
    found = on_disk[fixture]
    unidentified.extend(
        f"{fixture} scenario at index {index}"
        for index, (_, missing) in enumerate(found)
        if missing
    )
    if fixture not in opened:
        # Not opened at all: the fixture-level guard above already reported it, or
        # KNOWN_UNCOVERED excused the whole file. Either way the scenarios inside it
        # are not this check's finding to make twice.
        continue
    fixtures_checked += 1
    for scenario_id, _ in found:
        scenarios_total += 1
        if (fixture, scenario_id) in replayed:
            scenarios_replayed += 1
            continue
        if (fixture, scenario_id) in excuses:
            continue
        errors.append(
            f"ERROR: scenario '{scenario_id}' of '{fixture}' was NOT replayed, though the "
            "suite opened the fixture.\n"
            "       Opening a fixture is not replaying every scenario in it — one scenario "
            "is enough\n"
            "       to satisfy the coverage guard above. Replay it, or add it to "
            "KNOWN_UNREPLAYED_SCENARIOS\n"
            "       with a reason naming what this binding cannot express."
        )

# The ledger's own drift: an id the runner recorded that the fixture does not carry.
for fixture, scenario_id in sorted(replayed):
    if fixture not in on_disk:
        errors.append(
            f"ERROR: the ledger records scenario '{scenario_id}' of '{fixture}', which "
            "carries no scenarios array."
        )
        continue
    if scenario_id not in {found_id for found_id, _ in on_disk[fixture]}:
        errors.append(
            f"ERROR: the ledger records scenario '{scenario_id}' of '{fixture}', which the "
            "fixture does not carry.\n"
            "       The runner and the corpus disagree about how a scenario is named, so "
            "the ledger is\n"
            "       recording something nobody can check."
        )

# A stale excuse, in both directions — same rule as KNOWN_UNCOVERED one layer up.
for (fixture, scenario_id), reason in sorted(excuses.items()):
    if fixture not in on_disk:
        errors.append(
            f"ERROR: KNOWN_UNREPLAYED_SCENARIOS lists '{fixture}|{scenario_id}', but that "
            "fixture is not in the canonical corpus or carries no scenarios."
        )
        continue
    if scenario_id not in {found_id for found_id, _ in on_disk[fixture]}:
        errors.append(
            f"ERROR: KNOWN_UNREPLAYED_SCENARIOS lists '{fixture}|{scenario_id}', which the "
            "fixture does not carry.\n"
            f"       The excuse is stale: it names an id that does not exist. Its ids are "
            f"{sorted(found_id for found_id, _ in on_disk[fixture])}."
        )
        continue
    if (fixture, scenario_id) in replayed:
        errors.append(
            f"ERROR: KNOWN_UNREPLAYED_SCENARIOS lists '{fixture}|{scenario_id}', but the "
            "suite REPLAYED it.\n"
            "       The excuse is stale and now hides nothing, so its reason is a lie: "
            f"\"{reason}\".\n"
            "       Delete the entry. An allowlist that outlives its gap understates "
            "coverage silently."
        )

# ---- Prose-key verification rung (#lzprosekeyconvention, rule 8) ------------
#
# Rules 1-7 live inside the test host and are all satisfied over an EMPTY
# population: a fixture that is opened and then never replayed discharges nothing,
# contradicts nothing, and passes every one of them. That is the same vacuity the
# corpus's own `anti_vacuity` keys exist to name, reappearing in the guard meant to
# enforce them — and it is invisible from inside a test that never ran.
#
# The required set is DERIVED from the corpus above, never from a count kept here.
# Two-directional, exactly as the ledgers one rung up: a declaring fixture the suite
# opened but never verified is a gap, and a verification record for a fixture that
# does not declare prose is drift in the recorder.
for fixture in sorted(declaring):
    if fixture not in opened:
        # Not opened at all: rung 1 already reported it, or KNOWN_UNCOVERED excused
        # the whole file. Not this check's finding to make twice.
        continue
    if fixture in prose_verified:
        continue
    errors.append(
        f"ERROR: '{fixture}' declares `assertions.prose`, and the suite OPENED it, but no "
        "runner ever verified its discharges.\n"
        "       Rules 1-7 are vacuously green over a fixture nobody replayed: it discharges\n"
        "       nothing, so nothing can contradict it. Replay it under ProseLedger.Replay and\n"
        "       call VerifyProse."
    )

for fixture in sorted(prose_verified):
    if fixture not in declaring:
        errors.append(
            f"ERROR: the ledger records a prose verification for '{fixture}', which declares "
            "no `assertions.prose`.\n"
            "       The recorder and the corpus disagree about which fixtures carry paragraphs,\n"
            "       so this rung is measuring something nobody can check."
        )

for entry in unidentified:
    errors.append(
        f"ERROR: {entry} carries neither `id` nor `name`.\n"
        "       The ledger would record it by POSITION, which silently rebinds on a corpus\n"
        "       reorder. Give it a stable id upstream in lazily-spec (#lzspecscenarioids)."
    )

if errors:
    for error in errors:
        print(error, file=sys.stderr)
    print(f"scenario replay coverage FAILED: {len(errors)} problem(s)", file=sys.stderr)
    sys.exit(1)

# ---- Positive-evidence floor (#lzvacuousrun) ----
# Everything above walks the scenarios that OPENED fixtures carry, so an empty
# population satisfies all of it: zero scenarios means zero unreplayed scenarios,
# zero ledger drift, and zero stale excuses. Assert the magnitude before claiming
# green — "nothing was wrong" and "nothing was compared" must not print the same
# line.
#
# The floor is REQUIRED, not defaulted. A default here would mean the one case
# this block exists to catch — the caller failed to pass the floor through —
# lands in the same branch as "the floor is satisfied", and a guard that cannot
# read its own threshold must never be the guard that says OK.
if "MIN_SCENARIOS" not in os.environ:
    print(
        "FAIL: MIN_SCENARIOS was not passed through to the scenario guard.\n"
        "      Without it the positive-evidence floor cannot be evaluated, and a\n"
        "      floor that cannot be read is not a floor.",
        file=sys.stderr,
    )
    sys.exit(1)
min_scenarios = int(os.environ["MIN_SCENARIOS"])
if scenarios_total == 0:
    print(
        "ERROR: ZERO scenarios were found across the opened fixtures.\n"
        "       The rung above is vacuously green over an empty population — no\n"
        "       scenario can go unreplayed when none exist (#lzvacuousrun). Either\n"
        "       the corpus carries no `scenarios` arrays, or no scenario-bearing\n"
        "       fixture was opened. Neither is coverage.",
        file=sys.stderr,
    )
    sys.exit(1)
if scenarios_replayed < min_scenarios:
    print(
        f"ERROR: only {scenarios_replayed} distinct scenarios were REPLAYED, expected "
        f">= {min_scenarios}.\n"
        "       A scenario dispatch stopped matching, or the runtime ledger detached\n"
        "       mid-run. Do not lower MIN_SCENARIOS to fix this.",
        file=sys.stderr,
    )
    sys.exit(1)

print(
    f"scenario replay coverage OK: {scenarios_replayed}/{scenarios_total} scenarios across "
    f"{fixtures_checked} opened fixtures REPLAYED "
    f"({len(excuses)} listed as known-unreplayed; floor {min_scenarios}; runtime ledger — "
    "these scenarios were really reached)"
)
print(
    f"prose-key discharge OK: {len(declaring & opened)}/{len(declaring)} canonical fixtures "
    "declaring `assertions.prose` reached VerifyProse "
    "(required set derived from the corpus, not from a count kept here)"
)
PY
)"

MIN_SCENARIOS="$MIN_SCENARIOS" \
python3 -c "$SCENARIO_GUARD_PY" "$SPEC_DIR" "$MANIFEST" \
  ${KNOWN_UNREPLAYED_SCENARIOS[@]+"${KNOWN_UNREPLAYED_SCENARIOS[@]}"}
