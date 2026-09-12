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

# ---- Shared diagnostics for every python leg (#lzcorpusabsencehandling) ------
#
# Every rung in this script fails with a sentence that names the file, says what
# is wrong with it, and says what to do. The python legs did not: an input that
# was PRESENT but unparseable — a truncated corpus fixture, exactly what a
# perturbation probe leaves behind when it is interrupted — died on an unhandled
# `json.decoder.JSONDecodeError` and handed the operator a stack trace that did
# not even name the fixture. That was a DIAGNOSABILITY gap, never a correctness
# one: the exit code was always right and nothing passed that should have failed.
# It surfaced during #lzoverrideallrunnersaudit, beside a legitimate finding, and
# made the run harder to read than the finding was.
#
# The distinction these helpers PRESERVE is the one the bash legs above and below
# already draw:
#
#   * a corpus that is ABSENT is a local-checkout state, decided by CONTEXT (CI,
#     LAZILY_CONFORMANCE_STRICT, an explicit override) further down;
#   * a file that is PRESENT but unreadable or unparseable is a BROKEN INPUT —
#     under an explicit LAZILY_SPEC_CONFORMANCE_DIR, a broken PROBE — and stays a
#     HARD failure. Legible is not lenient, and neither a warning nor a skip is
#     available here: coverage computed over a corpus that could not be fully
#     read is untrustworthy in both directions, which is the same reasoning the
#     manifest self-check makes one rung up.
#
# They live in ONE prelude prepended to all three legs rather than at the one
# call site that crashed, because the exposure was never specific to that call:
# both corpus readers, both manifest readers, and both integer floors could each
# have produced a traceback where a message belongs.
PY_DIAG="$(cat <<'PY'
import json
import os
import sys


def die(lines, code=1):
    for line in lines:
        print(line, file=sys.stderr)
    sys.exit(code)


# ---- The evidence must be THIS run's (#lzstalemanifest) ----------------------
#
# `SpecCorpus.RunIdMarker`. The recorder writes one such line per flushing test
# host, carrying the id of the `make check` invocation that produced the lines
# beneath it; every leg that reads the manifest requires it to equal the id of the
# invocation asking. See the bash rung of the same name for why a stamp written by
# the producer is the only kind worth checking.
RUN_ID_MARKER = "# lazily-run-id"
STALE_OK_VAR = "LAZILY_CONFORMANCE_STALE_EVIDENCE_OK"
RUN_ID_VAR = "LAZILY_CONFORMANCE_RUN_ID"


def require_run_id(path, text, what="conformance manifest"):
    """Refuse evidence that is not this invocation's, by name and by id.

    Duplicated deliberately across the bash leg and both python legs rather than
    checked once up front. Each of these is a GUARD that reads the evidence file
    and asserts something about what the run did; a freshness check that lives in
    only one of them is a freshness check that a reordering, or a python leg run
    on its own, silently drops. The cost is one pass over a file already in memory.
    """
    if os.environ.get(STALE_OK_VAR, ""):
        return
    want = os.environ.get(RUN_ID_VAR, "")
    if not want:
        die([
            f"ERROR: {RUN_ID_VAR} is unset, so this guard cannot tell whether",
            f"       {what} '{path}' is THIS run's evidence or an older run's.",
            "       It REFUSES rather than skipping: accepting unstamped evidence when the",
            "       variable happens to be unset is the same hole with an extra step",
            "       (#lzstalemanifest). Run the gate through `make check`, which generates",
            "       one id per invocation and passes it to both the test step that writes",
            f"       the evidence and every guard that reads it. To audit an OLD manifest on",
            f"       purpose, set {STALE_OK_VAR}=1 and read the banner it prints.",
        ])
    found = [
        line[len(RUN_ID_MARKER):].strip()
        for line in text.splitlines()
        if line.startswith(RUN_ID_MARKER)
    ]
    if not found:
        die([
            f"ERROR: {what} '{path}' carries no `{RUN_ID_MARKER}` line.",
            "       Unstamped evidence has unknown provenance — it predates",
            "       #lzstalemanifest, or the suite ran without LAZILY_CONFORMANCE_RUN_ID",
            "       set, or nothing was recorded at all. Re-run `make check`.",
        ])
    wrong = sorted({value for value in found if value != want})
    if wrong:
        die([
            f"ERROR: {what} '{path}' is a DIFFERENT run's evidence.",
            f"       wanted run id: {want}",
            "       found run id(s): " + ", ".join(wrong),
            "       Every rung here asserts what the run DID; on these bytes it would be",
            "       asserting what some earlier run did. Re-run the suite so the recorder",
            "       rewrites the manifest under this invocation's id (#lzstalemanifest).",
        ])


def diagnose(what, exc, advice=()):
    """The message lines for an input that is PRESENT but unusable.

    `what` is a noun phrase naming the thing, path included — the fault is
    useless without it, and the traceback this replaced printed no path at all.
    """
    if isinstance(exc, UnicodeDecodeError):
        problem = f"is not valid UTF-8: {exc}"
    elif isinstance(exc, ValueError):
        problem = f"is not valid JSON: {exc}"
    else:
        problem = f"could not be read: {exc}"
    return [
        f"ERROR: {what} {problem}",
        "       It is PRESENT but unusable, which is a BROKEN INPUT and never the",
        "       absent-corpus case this script skips on a laptop with no sibling",
        "       checkout. A broken input stays a hard failure (#lzcorpusabsencehandling).",
        *advice,
    ]


def read_text(path, what, advice=(), code=1, errors="strict"):
    try:
        with open(path, "r", encoding="utf-8", errors=errors) as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError) as exc:
        die(diagnose(f"{what} '{path}'", exc, advice), code=code)


def read_int(raw, name, advice=()):
    """A floor that cannot be read is not a floor — and must say so in words."""
    try:
        return int(raw)
    except (TypeError, ValueError):
        die([
            f"ERROR: {name}={raw!r} is not an integer.",
            "       A floor that cannot be parsed cannot be evaluated, and a guard that",
            "       cannot read its own threshold must never be the guard that says OK.",
            *advice,
        ])
PY
)"

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
        # `errors="replace"` on purpose: an odd byte in a C# source is not this
        # rung's finding, and a decode fault must not stop the scan. An OSError
        # still is one — a source the walk FOUND but could not open leaves the
        # scan reporting on fewer files than it examined, which is the vacuous
        # pass the floor below exists to refuse — so it fails by NAME rather
        # than by traceback.
        text = read_text(
            p,
            "C# source",
            (
                "       The corpus-root scan cannot report on a file it could not read,",
                "       and reporting OK without it is a pass over an unexamined source",
                "       (#lzvacuousrun). Fix the file or its permissions.",
            ),
            code=2,
            errors="replace",
        )
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

corpus_root_report="$(collect_sources | python3 -c "$PY_DIAG
$CORPUS_ROOT_PY" "$CORPUS_ROOT_ALLOW")"
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
# EXACT: 151 is what a green CI run on the current corpus actually opens, with
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
# Re-pinned for lazily-spec f89d865, which carries the three replay-equivalence
# fixtures this binding now replays through `ReplayConformanceTests`
# (`conformance coverage OK: 151/156`), matching a local green `make check`.
# Verified exact: 152 fails this floor.
MIN_FIXTURES="${MIN_FIXTURES:-151}"

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
# EXACT: 169 is what a green CI run on the current corpus actually replays,
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
# Re-pinned for lazily-spec f89d865 (`scenario replay coverage OK: 169/169`),
# matching a local green `make check`. The three replay fixtures carry `steps`
# rather than `scenarios`, so the move here is corpus growth elsewhere rather
# than the new runner. Verified exact: 170 fails this floor.
MIN_SCENARIOS="${MIN_SCENARIOS:-169}"

MANIFEST="${LAZILY_CONFORMANCE_MANIFEST:-build/conformance-fixtures-loaded.txt}"
if [ ! -s "$MANIFEST" ]; then
  echo "FAIL: no conformance manifest at $MANIFEST." >&2
  echo "      Run the suite with LAZILY_CONFORMANCE_MANIFEST set so the recorder" >&2
  echo "      attaches. An absent manifest is missing evidence, not evidence of" >&2
  echo "      absence." >&2
  exit 1
fi
# ---- RUNG: the evidence must be THIS run's (#lzstalemanifest) ---------------
#
# Everything below — and both python legs — reads this ONE file and asserts
# something about what the run DID: which fixtures were opened, which scenarios
# were reached, which assertion blocks were declared and bound. None of that is a
# property of the bytes; it is a property of the run that wrote them.
#
# And the writer is a DIFFERENT PROCESS from every reader. The recorder lives in
# the `dotnet test` host; this script is a later target in `make check`. The only
# thing that used to make the file this run's was the `: >` truncation at the top
# of the `test` recipe — a convention, re-spelled by hand in ci.yml, and absent
# from every other path that reaches this script. `make conformance-coverage`
# alone, or this script run directly, read whatever run last wrote the file and
# reported it as the current one. That is not a hypothetical: `dotnet test` never
# caches test RESULTS, so unlike lazily-kt's `:test UP-TO-DATE` the stale read
# here arrives through invoking the guard without the test step, not through a
# skipped one — and rung 0 is the ONLY thing that sees a detached bind (the suite
# stays green at 325 + 7 with a bind deleted), so its evidence being this run's is
# the whole of its value.
#
# The stamp is written by the RECORDER, not by the shell that truncates: a stamp
# written here would attest that the truncation ran, while the claim the guards
# make is about what the test host recorded.
RUN_ID_MARKER='# lazily-run-id'

# The named opt-out (#lzstalemanifest). The mutation-probe workflow for the pins
# in this file — `EXPECTED_LEDGERED_BLOCKS=1 ./scripts/check-conformance-coverage.sh`
# against a manifest an earlier `make check` wrote — is a legitimate reason to
# read an old run's evidence, and the alternative dodge is worse: copying the id
# out of the manifest into the environment is the forgery this rung exists to
# refuse, wearing an operator's hands. So the escape is EXPLICIT, exactly `1`,
# loud on stderr, and REFUSED under CI, where trusting unverified evidence is
# never a thing anybody meant.
STALE_EVIDENCE_OK="${LAZILY_CONFORMANCE_STALE_EVIDENCE_OK:-}"
if [ -n "$STALE_EVIDENCE_OK" ] && [ "$STALE_EVIDENCE_OK" != "1" ]; then
  echo "FAIL: LAZILY_CONFORMANCE_STALE_EVIDENCE_OK=$STALE_EVIDENCE_OK is not \`1\`." >&2
  echo "      This is an opt-out from the run-id freshness rung, so it takes ONE" >&2
  echo "      value and refuses every other. A typo that reads as truthy would" >&2
  echo "      silently disable the rung." >&2
  exit 1
fi
if [ -n "$STALE_EVIDENCE_OK" ] && [ -n "${CI:-}" ]; then
  echo "FAIL: LAZILY_CONFORMANCE_STALE_EVIDENCE_OK is set and CI=$CI." >&2
  echo "      The opt-out exists for a local mutation probe against an old manifest." >&2
  echo "      In CI it would turn every 'these bytes were really read' claim in this" >&2
  echo "      script back into 'some run really read them' (#lzstalemanifest)." >&2
  exit 1
fi
if [ -n "$STALE_EVIDENCE_OK" ]; then
  echo "WARNING: LAZILY_CONFORMANCE_STALE_EVIDENCE_OK=1 — the run-id rung is OFF and" >&2
  echo "         every rung below is reporting on WHOEVER wrote $MANIFEST, which may" >&2
  echo "         not be this run. Do not read a green here as a green build." >&2
else
  CONFORMANCE_RUN_ID="${LAZILY_CONFORMANCE_RUN_ID:-}"
  if [ -z "$CONFORMANCE_RUN_ID" ]; then
    echo "FAIL: LAZILY_CONFORMANCE_RUN_ID is unset, so nothing here can tell whether" >&2
    echo "      $MANIFEST is THIS run's evidence or an older run's." >&2
    echo "      It REFUSES rather than skipping (#lzstalemanifest): a guard that accepts" >&2
    echo "      unstamped evidence whenever the variable is unset is the same hole with" >&2
    echo "      an extra step. Run the gate as \`make check\`, which generates one id per" >&2
    echo "      invocation and hands it to the test step that WRITES this file and to" >&2
    echo "      every guard that READS it." >&2
    exit 1
  fi
  # A run id is compared by byte equality and appears inside the evidence file, so
  # it must not be able to carry a newline or a leading/trailing blank: an id of
  # "A<newline>B" would have the recorder write a stamp line plus a forged bare
  # line, and the comparison below would be against something no shell variable
  # can hold cleanly. Refused up front, where the operator can see why.
  case "$CONFORMANCE_RUN_ID" in
    *[!0-9A-Za-z._:-]*)
      echo "FAIL: LAZILY_CONFORMANCE_RUN_ID='$CONFORMANCE_RUN_ID' contains a character" >&2
      echo "      outside [0-9A-Za-z._:-]. The id is written into $MANIFEST as one line" >&2
      echo "      and compared byte for byte; whitespace or a newline in it would forge" >&2
      echo "      extra evidence lines and make the comparison meaningless." >&2
      exit 1
      ;;
  esac
  STAMPS="$(awk -v m="$RUN_ID_MARKER" 'index($0, m) == 1 { print substr($0, length(m) + 2) }' "$MANIFEST")"
  if [ -z "$STAMPS" ]; then
    echo "FAIL: $MANIFEST carries no \`$RUN_ID_MARKER\` line." >&2
    echo "      Unstamped evidence has unknown provenance: it predates this rung, or the" >&2
    echo "      suite ran without LAZILY_CONFORMANCE_RUN_ID set, or the recorder never" >&2
    echo "      attached. Missing provenance is missing EVIDENCE, never evidence that the" >&2
    echo "      run was fine (#lzstalemanifest). Re-run \`make check\`." >&2
    exit 1
  fi
  # EVERY stamp, not the first. The manifest is a UNION across however many test
  # hosts append to it, and a second host writing under a stale id is the same
  # defect as a stale file.
  FOREIGN="$(printf '%s\n' "$STAMPS" | sort -u | grep -vxF "$CONFORMANCE_RUN_ID" || true)"
  if [ -n "$FOREIGN" ]; then
    echo "FAIL: $MANIFEST is a DIFFERENT run's evidence." >&2
    echo "      wanted run id: $CONFORMANCE_RUN_ID" >&2
    echo "      found run id(s): $(printf '%s' "$FOREIGN" | tr '\n' ' ')" >&2
    echo "      Every rung here asserts what the run DID; on these bytes it would be" >&2
    echo "      asserting what an earlier run did. Re-run the suite so the recorder" >&2
    echo "      rewrites this file under this invocation's id (#lzstalemanifest)." >&2
    exit 1
  fi
fi

# Stamp lines are provenance, not records: dropped here so no rung below can count
# one as a fixture.
OPENED="$(grep -v "^$RUN_ID_MARKER" "$MANIFEST" | sort -u || true)"

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
# `SpecCorpus.Load` inventories every assertion block at read time — a general
# walk of the fixture, at any depth, over every name a runner binds (`assertions`,
# `expect`, `expected`) — and the `FixtureAssertions` constructor books one as
# BOUND, both riding this same manifest under `blocks-` prefixes. The two sides are
# matched by CONTENT digest and never by the block's label: runners spell those
# inconsistently, and a label-keyed ledger would silently miss the mismatch rather
# than report it.
#
# The inventory was FIXED-PLACE until #lzunboundblockguard: the root `assertions`
# plus one per element of `frames`/`scenarios`/`rejects`. That found 33 blocks in a
# corpus carrying 690, and reported "33/33 bound, OK" while every per-step `expect`
# in the stdlib and reactive-graph corpora was bound by nothing — rung 0 wearing the
# null form it exists to catch. Widening it surfaced seven scenarios whose `expect`
# the runner replaced with hand-written literals (crdt-tree/algebra scenarios 1-2,
# reliable-sync coalesce 0-1 and lease-eviction 1-3).
#
# Content keying has one known consequence, and it is recorded rather than papered
# over: two sites carrying IDENTICAL bytes share a digest, so binding one marks
# both. That is bounded by the rung below — a scenario nobody replayed is a
# scenario-ledger failure — and the alternative, keying by the runner's own label,
# is what this ledger exists NOT to do.
#
# An unbindable block belongs HERE, as a documented excuse read on every run, not
# as a runner fabricated to manufacture coverage. Format: "fixture|where|reason".
# Two-directional, exactly like KNOWN_UNCOVERED and ExcuseKey: an excuse for a block
# this run DID bind fails as stale, and so does one naming a site no opened fixture
# declares. An excuse nothing can falsify is an allowlist entry wearing a hat.
#
# Under a SIZE PIN as well as a set equality (#lzledgerceiling, #lzledgerratchet):
# `EXPECTED_LEDGERED_BLOCKS` below is 0, this array is empty, and the two must be
# EXACTLY equal — growth and shrink both fail. Set equality alone is satisfied by
# any CONSISTENT pair — detach N binds, write the N entries, and both directions
# pass — because it compares the ledger against the RUN and both sides move
# together. The pin compares it against a COMMITTED CONSTANT, which does not move.
KNOWN_UNBOUND_BLOCKS=(
)

BLOCK_GUARD_PY="$(cat <<'PY'
import json
import os
import sys

manifest_path, spec_dir = sys.argv[1], sys.argv[2]

excuses = {}
for raw in sys.argv[3:]:
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

# ---- The excused set's SIZE, pinned as an EXACT equality (#lzledgerceiling,
#      sharpened to a ratchet by #lzledgerratchet)
#
# The three rungs below assert this ledger as a SET EQUALITY against the run, and
# they fail in BOTH directions: an unbound site nobody excused fails, and an excuse
# the run outlived fails. That is strictly stronger than an allowlist, and it is
# still satisfied by ANY CONSISTENT PAIR. A commit that detaches N binds and writes
# the N matching entries agrees with itself and passes both directions. The derived
# SITE and DIGEST equalities further down miss it too: a detached site is still
# DECLARED by the corpus walk — it has merely stopped being bound — so both
# magnitudes hold at 743 and 634 while the bound population drops.
#
# lazily-rs demonstrated the hole by dropping one bound record and adding its
# matching entry; lazily-cs reproduced it by deleting the `arena_blob.json`
# `assertions` bind — the very site #lznullformblind was named for — and excusing it.
# Every other rung stayed green.
#
# What closes it is a size pinned against a COMMITTED CONSTANT. The set equality
# compares the ledger against the RUN, and under the attack both sides move
# together; a constant does not move, and that independence is the whole value.
#
# The operator has to be an EQUALITY, not a ceiling, and this is the correction
# #lzledgerratchet landed. A `<=` ceiling REFUSES the attack only while its slack
# is zero. Migrate one site and the ledger shrinks; nothing forces the constant
# down in the same commit, because shrinking is the good direction and a ceiling
# permits it silently; slack becomes 1, and the same detach-and-excuse commit
# passes again. Repeat per migration and the ceiling converges on the exact defect
# it replaced — a hand-typed floor sitting far under reality, never firing, and so
# never updated. The defect was never "a number exists"; it was "a number with
# slack". An equality has no slack by construction and cannot drift silently,
# because a stale value FAILS. A number that fails when stale is a ratchet.
#
# So BOTH directions are findings a person must see. Growth means an excuse was
# added. Shrink means sites were migrated and the pin was not lowered in the same
# commit — which is exactly how the slack above would start accumulating.
#
# Raising this line is legitimate, for a genuinely unbindable block — a corpus that
# gains a fixture no capability here can reach is the real case — but it must be
# deliberate and visible in the diff, with a reason, and expect to be asked why the
# capability cannot exist. Never raise it to park a block a runner could bind today:
# that is the laundering this rung exists to refuse.
#
# Env-overridable so it can be mutation-checked, and it FAILS CLOSED: a value that
# is not a non-negative integer is an unreadable pin, and a pin that cannot be read
# is not a pin. It never falls back to the committed default.
_PIN_RAW = os.environ.get("EXPECTED_LEDGERED_BLOCKS", "0")
# ONE parse for the whole family (#lzpinparsestrict): a NON-EMPTY run of bare
# ASCII digits `0`-`9`, and nothing else. Validated BEFORE any parse runs, and
# deliberately stricter than both `int()` and `str.isdigit()`, because each of
# those silently accepts a number nobody wrote: `int("1_0")` is 10 (PEP 515
# separators), `int(" 7 ")` is 7, and `"\u0663".isdigit()` is true for the
# Arabic-Indic three. This file used to read the pin with a bare `int()`, so all
# three of those got through. Refused now: whitespace around or inside, a leading
# `+` or `-`, separators, a radix prefix, a float or an exponent, and any
# non-ASCII digit. A negative falls out of the same check — no ledger size can
# equal it, so it would make this rung unsatisfiable rather than exact. Leading
# zeros are fine and `0` stays valid; this binding pins at zero.
#
# An UNSET variable takes the committed literal above. An EXPLICITLY EMPTY one is
# a REJECTION, not a fall-through to it: `os.environ.get(NAME, DEFAULT)`
# distinguishes the two, and whoever exported the wrong thing is the one person
# who cannot see that it was ignored.
if not _PIN_RAW or _PIN_RAW.strip("0123456789"):
    print(
        f"ERROR: EXPECTED_LEDGERED_BLOCKS={_PIN_RAW!r} is not a non-negative integer\n"
        "       in bare ASCII digits (#lzpinparsestrict).\n"
        "       This pin is the committed size of KNOWN_UNBOUND_BLOCKS and is asserted\n"
        "       EQUAL to it. An unreadable pin fails closed rather than falling back to\n"
        "       the default — not even an empty one: silently guarding a different\n"
        "       number than the one the operator typed is how a gate stops meaning what\n"
        "       it says.",
        file=sys.stderr,
    )
    sys.exit(1)
EXPECTED_LEDGERED_BLOCKS = int(_PIN_RAW)

if len(excuses) != EXPECTED_LEDGERED_BLOCKS:
    if len(excuses) < EXPECTED_LEDGERED_BLOCKS:
        print(
            f"ERROR: the unbound-block ledger SHRANK to {len(excuses)} entr(ies); the pin\n"
            f"       EXPECTED_LEDGERED_BLOCKS is still {EXPECTED_LEDGERED_BLOCKS}. Sites were\n"
            "       migrated and the pin was not lowered in the same commit.\n"
            f"       LOWER THE PIN TO {len(excuses)} IN THIS COMMIT.\n"
            "       Shrinking is the good direction, and it is still a failure on purpose.\n"
            "       A ceiling would have permitted this silently and kept the old number,\n"
            "       leaving one unit of SLACK — and slack is what disables this rung: the\n"
            "       next commit that detaches a bind and writes its matching entry fits\n"
            "       inside the margin and passes. Accumulate that per migration and the\n"
            "       pin converges on a stale floor that can never fire. An equality has no\n"
            "       margin to hide in, so every migration re-pins as it lands.",
            file=sys.stderr,
        )
        sys.exit(1)
    print(
        f"ERROR: the unbound-block ledger GREW to {len(excuses)} assertion block site(s)\n"
        f"       against a pin of {EXPECTED_LEDGERED_BLOCKS}. An excuse was added.\n"
        "       The set equality below cannot see this. It checks only that the ledger and\n"
        "       the RUN agree, which any consistent pair satisfies — a commit that detaches\n"
        "       binds and writes the matching entries passes it in BOTH directions — and\n"
        "       the derived site/digest magnitudes do not move either, because a detached\n"
        "       site is still DECLARED by the corpus. Only a size compared against a\n"
        "       COMMITTED CONSTANT sees it, because the constant does not move with the run.\n"
        "       Bind the block with FixtureAssertions.Of/Wrap. Raise the pin only for a\n"
        "       genuinely unbindable one, with a reason, in the same diff:",
        file=sys.stderr,
    )
    LISTED = 20
    for site, reason in sorted(excuses.items())[:LISTED]:
        print(f"         {site} — \"{reason}\"", file=sys.stderr)
    if len(excuses) > LISTED:
        print(
            f"         ... and {len(excuses) - LISTED} more — read the rest out of\n"
            "         `git diff -- scripts/check-conformance-coverage.sh`.",
            file=sys.stderr,
        )
    sys.exit(1)

# The manifest is EVIDENCE, and evidence that cannot be decoded is not evidence
# of absence. The bash leg proved it non-empty; bytes the recorder interleaved or
# truncated at process exit — the exact failure the fixture self-check below
# reasons about — would otherwise surface here as a UnicodeDecodeError traceback
# instead of a sentence naming the file (#lzcorpusabsencehandling).
MANIFEST_ADVICE = (
    "       Re-run the suite with LAZILY_CONFORMANCE_MANIFEST set to an ABSOLUTE",
    "       path, truncated first (what `make test` does), so the recorder rewrites",
    "       this file. Do not hand-edit it: it is the run's own evidence.",
)

declared = {}
bound = set()
manifest_text = read_text(manifest_path, "conformance manifest", MANIFEST_ADVICE)
# This leg re-reads the evidence, so it re-checks the provenance (#lzstalemanifest).
# The bash rung above already refused a foreign manifest; a freshness check that
# lives in only one reader is one a reordering silently drops, and this python
# block is a guard in its own right.
require_run_id(manifest_path, manifest_text)
for line in manifest_text.splitlines():
    if line.startswith(RUN_ID_MARKER):
        continue
    parts = line.split("\t")
    if parts[0] == "blocks-declared" and len(parts) == 4:
        declared.setdefault(parts[2], set()).add(f"{parts[1]}|{parts[3]}")
    elif parts[0] == "blocks-bound" and len(parts) == 2:
        bound.add(parts[1])

unbound = []
bound_sites = set()
declared_sites = set()
for digest, sites in sorted(declared.items()):
    declared_sites |= sites
    if digest in bound:
        bound_sites |= sites
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

# The OTHER direction. An excuse is a claim that a block CANNOT be bound here; a run
# that binds it has falsified the claim, and leaving it in the array means the next
# unbindable block at that site is excused by a reason that has become a lie. Same
# contract as ExcuseKey and KNOWN_UNCOVERED: a fixed gap fails the build until the
# entry is deleted.
stale = sorted(site for site in excuses if site in bound_sites)
if stale:
    print(
        f"ERROR: {len(stale)} KNOWN_UNBOUND_BLOCKS entr(ies) name a block this run DID\n"
        "       bind. The excuse hides nothing and its reason is now false:",
        file=sys.stderr,
    )
    for site in stale:
        print(f"         {site} — \"{excuses[site]}\"", file=sys.stderr)
    print("       Delete each one.", file=sys.stderr)
    sys.exit(1)

# And the third: an excuse for a site the opened corpus does not carry at all. The
# fixture was renamed, the path moved, or the block was deleted upstream — either
# way the entry now excuses nothing and would silently keep excusing nothing.
unknown = sorted(site for site in excuses if site not in declared_sites)
if unknown:
    print(
        f"ERROR: {len(unknown)} KNOWN_UNBOUND_BLOCKS entr(ies) name a block no OPENED\n"
        "       fixture declares — the fixture, the path, or the block is gone:",
        file=sys.stderr,
    )
    for site in unknown:
        print(f"         {site} — \"{excuses[site]}\"", file=sys.stderr)
    print(
        "       Delete each one, or fix its `fixture|where` to the path the walk prints.",
        file=sys.stderr,
    )
    sys.exit(1)

# ---- The expected site count is DERIVED, and it is not a floor (#lzblockfloorpin)
#
# This used to be a hand-typed `MIN_BLOCKS` constant compared with `>=`. Its own
# re-pin history is the argument against it: 692 -> 740 -> 743, each move a human
# reading the number the guard printed and copying it back into the source, and the
# last one ("lazily-spec 4010d99 grew `replay/canonical_encoding_equality.json` from
# 11 steps to 14") landed only because somebody happened to be looking. A constant
# that must be re-typed whenever the corpus moves is a constant that is WRONG for
# the whole interval between the corpus moving and someone noticing — and a `>=`
# comparison makes that interval invisible, because corpus GROWTH never trips it.
# Every drift this rung is supposed to report arrives as growth first.
#
# So the expectation is computed here instead, from two things this repo can be held
# to: (a) the canonical corpus DIRECTORY LISTING under $SPEC_DIR, and (b) this
# binding's own committed ledger, `KNOWN_UNCOVERED`, which is the same subtraction
# `MIN_FIXTURES` is checked against one rung up. Corpus listing minus ledger is the
# opened set; the walk below inventories that set's assertion blocks the way
# `SpecCorpus.DeclareWalk` inventories them at load time, and the two counts must be
# EQUAL.
#
# Deliberately NOT derived from the manifest, and not from what the run read. A
# manifest-derived expectation moves WITH the actual count, so a loader that detaches
# takes the expectation to 0 alongside it and this rung reports "0 == 0, OK" over a
# run that opened nothing — #lzvacuousrun exactly, wearing a derivation instead of a
# constant. The whole point of the number is that it comes from somewhere the run
# cannot influence.
#
# The walk mirrors the loader's rule and must keep mirroring it: the FAMILY block
# names {assertions, expect, expect_after, expect_initial, expected}; the OBJECT
# value of such a key, and one site per plain-OBJECT ELEMENT of an ARRAY value of
# one (#lzarrayelementsites); descent STOPS at a declared block, because what lives
# inside one is a key, not a block; and it counts SITES (fixture|where pairs), not
# distinct digests — two sites carrying identical bytes share one digest, so a digest
# count silently absorbs a deleted fixture whose blocks happen to be spelled like
# another's.
#
# Both halves of that rule were narrower until #lzarrayelementsites, and neither
# narrowness was hypothetical:
#
#   * the name tuple carried only {assertions, expect, expected}, while
#     `collections/semtree_incremental.json` holds six `expect_initial` /
#     `expect_after` blocks the semtree runner has always read and compared. 743/634
#     -> 749/640.
#   * an array-valued tracked key declared NOTHING, on the stated grounds that a
#     runner binds elements rather than the array — a site nobody emitted, while
#     `signaling/anti_spoof_session.json`'s runner bound and verified all 12 of its
#     per-step frame elements through `FixtureAssertions.Wrap`. 749/640 -> 761/652.
#
# This tuple and this walk must stay IDENTICAL to `SpecCorpus.AssertionBlockNames`
# and `SpecCorpus.DeclareWalk`: the two sides of the equality below are only
# comparable while they agree about what a block IS, and either side being the wider
# one makes a green run impossible rather than merely inaccurate.
ASSERTION_BLOCK_NAMES = (
    "assertions",
    "expect",
    "expect_after",
    "expect_initial",
    "expected",
)


def walk_sites(fixture_id, node, path, sites, blocks=None):
    """The python twin of `SpecCorpus.DeclareWalk` — see the paragraph above.

    `blocks`, when given, collects the block VALUES alongside their sites so the
    digest dimension can be derived from the same walk (#lzblocksitepin). One
    walk, two counts: a digest expectation derived by a second traversal could
    disagree with the site one and neither would be wrong about its own rule.
    """
    if isinstance(node, dict):
        for name, value in node.items():
            child = name if not path else path + "." + name
            if name in ASSERTION_BLOCK_NAMES:
                if isinstance(value, dict):
                    sites.add(f"{fixture_id}|{child}")
                    if blocks is not None:
                        blocks.append(value)
                    continue
                if isinstance(value, list):
                    # One site per plain-OBJECT element, exactly one level, by TRUE index
                    # (#lzarrayelementsites) — the twin of `SpecCorpus.DeclareArrayElements`.
                    # A non-object element gets no site and is WALKED instead, so `[[{..}]]`
                    # declares nothing for the inner object while a tracked key nested deeper
                    # inside it is still found.
                    for index, item in enumerate(value):
                        if isinstance(item, dict):
                            sites.add(f"{fixture_id}|{child}[{index}]")
                            if blocks is not None:
                                blocks.append(item)
                        else:
                            walk_sites(
                                fixture_id, item, f"{child}[{index}]", sites, blocks
                            )
                    continue
            walk_sites(fixture_id, value, child, sites, blocks)
    elif isinstance(node, list):
        for index, item in enumerate(node):
            walk_sites(fixture_id, item, f"{path}[{index}]", sites, blocks)


# `SpecCorpus.BlockDigest` from tests/Lazily.Tests/SpecCorpus.cs, rule for rule:
# FNV-1a over a type-tagged walk, object properties in document order, numbers
# folded by their RAW LEXICAL FORM (`JsonElement.GetRawText()`) rather than by
# any parsed value, and anything that is neither object, array, string, number
# nor boolean folded as `z`. A twin that normalised numbers would split one block
# into two and report the whole corpus unbound.
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
MASK = (1 << 64) - 1


class RawNumber:
    """A JSON number kept as the exact token the file carries."""

    __slots__ = ("raw",)

    def __init__(self, raw):
        self.raw = raw


def feed(hash_value, text):
    for byte in text.encode("utf-8"):
        hash_value ^= byte
        hash_value = (hash_value * FNV_PRIME) & MASK
    return hash_value


def hash_value(hash_state, value):
    if isinstance(value, dict):
        hash_state = feed(hash_state, "{")
        for name, item in value.items():
            hash_state = hash_value(feed(feed(hash_state, name), "="), item)
        return feed(hash_state, "}")
    if isinstance(value, list):
        hash_state = feed(hash_state, "[")
        for item in value:
            hash_state = hash_value(hash_state, item)
        return feed(hash_state, "]")
    if isinstance(value, str):
        return feed(feed(hash_state, "s"), value)
    if isinstance(value, RawNumber):
        return feed(feed(hash_state, "n"), value.raw)
    if value is True:
        return feed(hash_state, "b1")
    if value is False:
        return feed(hash_state, "b0")
    return feed(hash_state, "z")


def block_digest(block):
    return f"{hash_value(FNV_OFFSET, block):016x}"


corpus_fixtures = []
for walk_root, _walk_dirs, walk_names in os.walk(spec_dir):
    for walk_name in walk_names:
        if walk_name.endswith(".json"):
            corpus_fixtures.append(
                os.path.relpath(os.path.join(walk_root, walk_name), spec_dir).replace(os.sep, "/")
            )
corpus_fixtures.sort()

# The ledger arrives as a newline-joined scalar rather than as argv, so the excuse
# argv above keeps its shape, and so this script adds no second top-level bash array
# for lazily-spec's `check-corpus-floors.mjs` to have to classify.
uncovered_ledger = {
    entry.strip()
    for entry in os.environ.get("KNOWN_UNCOVERED_LEDGER", "").splitlines()
    if entry.strip()
}

expected_sites = set()
expected_blocks_walked = []
walked = 0
for fixture_id in corpus_fixtures:
    if fixture_id in uncovered_ledger:
        continue
    try:
        with open(os.path.join(spec_dir, fixture_id), encoding="utf-8") as handle:
            # `parse_int` / `parse_float` keep the RAW token, because that is what
            # `BlockDigest` folds.
            document = json.load(handle, parse_int=RawNumber, parse_float=RawNumber)
    except (OSError, ValueError) as error:
        print(
            f"ERROR: could not read canonical fixture '{fixture_id}' out of {spec_dir}: {error}\n"
            "       The expected block count is derived from these bytes, so an unreadable\n"
            "       fixture is missing EVIDENCE, not evidence of absence. Fix the checkout.",
            file=sys.stderr,
        )
        sys.exit(1)
    walk_sites(fixture_id, document, "", expected_sites, expected_blocks_walked)
    walked += 1

expected_digests = {block_digest(block) for block in expected_blocks_walked}

# Positive-evidence floor (#lzvacuousrun): an empty opened set derives zero expected
# sites, and zero == zero would report OK having compared nothing.
if walked == 0 or not expected_sites or not expected_digests:
    print(
        f"ERROR: the corpus at {spec_dir} minus KNOWN_UNCOVERED derived {walked} opened "
        f"fixture(s) carrying {len(expected_sites)} assertion block site(s).\n"
        "       An empty derivation makes this rung vacuously green: zero expected sites\n"
        "       are trivially matched by a run that inventoried nothing (#lzvacuousrun).\n"
        "       The checkout is wrong, or LAZILY_SPEC_CONFORMANCE_DIR points elsewhere.",
        file=sys.stderr,
    )
    sys.exit(1)

expected_blocks = len(expected_sites)
if len(declared_sites) != expected_blocks:
    direction = "FEWER than" if len(declared_sites) < expected_blocks else "MORE than"
    print(
        f"ERROR: the run inventoried {len(declared_sites)} assertion block SITES; the canonical\n"
        f"       corpus at {spec_dir} minus KNOWN_UNCOVERED derives {expected_blocks} over "
        f"{walked} opened\n"
        f"       fixtures. The run has {direction} the corpus declares.\n"
        "       This is an EQUALITY, not a floor: either the corpus moved under this\n"
        "       checkout (re-pull the lazily-spec sibling so both sides read the same\n"
        "       bytes), or the loader-side walk in SpecCorpus.DeclareWalk detached from\n"
        "       the rule spelled out above and stopped declaring sites it should.\n"
        "       There is no number to re-pin here — fix whichever side moved.",
        file=sys.stderr,
    )
    sys.exit(1)

# The OTHER dimension (#lzblocksitepin). Sites and distinct digests are each
# blind to what the other sees, in opposite directions. A site count absorbs a
# CONTENT edit: rewriting one block so it is spelled exactly like another's
# leaves 743 sites and takes the digest set from 634 to 633, and the corpus has
# genuinely lost a distinct claim. A digest count absorbs a DELETION of a block
# whose bytes recur elsewhere. Both derived from the one walk above, both
# asserted EQUAL.
if len(declared) != len(expected_digests):
    direction = "FEWER than" if len(declared) < len(expected_digests) else "MORE than"
    print(
        f"ERROR: the run inventoried {len(declared)} DISTINCT assertion-block digests; the\n"
        f"       canonical corpus at {spec_dir} minus KNOWN_UNCOVERED derives "
        f"{len(expected_digests)}\n"
        f"       over {walked} opened fixtures. The run has {direction} the corpus declares.\n"
        "       The SITE count above can agree while this does not: two sites spelled\n"
        "       identically share one digest, so a content edit that collapses two\n"
        "       distinct claims into one leaves the site count untouched.\n"
        "       Either the corpus moved under this checkout, or SpecCorpus.BlockDigest\n"
        "       and its twin in this script stopped agreeing — fix whichever moved.",
        file=sys.stderr,
    )
    sys.exit(1)

print(
    f"assertion-block bind OK: {len(bound_sites)}/{len(declared_sites)} assertion block sites "
    f"carried by opened fixtures were BOUND to a tracker ({len(excuses)} declared unbindable of "
    f"exactly {EXPECTED_LEDGERED_BLOCKS}, a pin asserted EQUAL in both directions, so an added "
    f"excuse and an unlowered pin after a migration both fail; "
    f"derived {expected_blocks} sites AND {len(expected_digests)} distinct digests from {walked} "
    f"opened fixtures, both asserted EQUAL; content-keyed, so a runner's block NAME cannot "
    f"satisfy it)"
)
PY
)"

KNOWN_UNCOVERED_LEDGER="$(printf '%s\n' ${KNOWN_UNCOVERED[@]+"${KNOWN_UNCOVERED[@]}"})" \
python3 -c "$PY_DIAG
$BLOCK_GUARD_PY" "$MANIFEST" "$SPEC_DIR" \
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

MANIFEST_ADVICE = (
    "       Re-run the suite with LAZILY_CONFORMANCE_MANIFEST set to an ABSOLUTE",
    "       path, truncated first (what `make test` does), so the recorder rewrites",
    "       this file. Do not hand-edit it: it is the run's own evidence.",
)

opened = set()
replayed = set()
prose_verified = set()
manifest_text = read_text(manifest_path, "conformance manifest", MANIFEST_ADVICE)
# Same reason as the block leg: every reader of this file checks that it is this
# run's (#lzstalemanifest).
require_run_id(manifest_path, manifest_text)
for line in manifest_text.splitlines():
    if not line:
        continue
    if line.startswith(RUN_ID_MARKER):
        # Provenance, not a record. A bare line otherwise reads as "this fixture
        # was OPENED", which would file the stamp as a fixture name.
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


def ids_of(document):
    """`id`, else `name` — the fixed resolution order, with no third step.

    The positional `#<n>` fallback is gone (#lzspecscenarioids). It let the ledger
    identify a scenario BY POSITION, which silently rebinds to a different scenario
    when the corpus array is reordered -- the ledger says "index 1 was replayed",
    this reader looks at whatever now sits at index 1, and the two agree with each
    other about the wrong thing. An unidentified scenario is reported, never given
    an invented id. A blank identifier is refused too: it would file every blank-id
    scenario under a single ledger entry.
    """
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


def declares_prose(document):
    """Does this fixture's `assertions` block declare prose keys (#lzprosekeyconvention)?

    Read from the CORPUS, never from a list kept here: rule 8's required set of
    verifications is derived, so a fixture that grows an `assertions.prose` array
    upstream starts demanding a discharge here without anyone updating a count.
    """
    if not isinstance(document, dict):
        return False
    assertions = document.get("assertions")
    if not isinstance(assertions, dict):
        return False
    return isinstance(assertions.get("prose"), list) and bool(assertions["prose"])


# A fixture the walk FOUND but could not parse is a broken input, and it is
# collected rather than raised (#lzcorpusabsencehandling). Two reasons the
# collection matters more than the catch: every broken fixture is named in ONE
# run, so an operator repairing a scratch corpus is not handed them one at a
# time; and the failure lands BEFORE any coverage arithmetic, because a count
# computed over a corpus that was only partly readable is wrong in both
# directions — understated by the scenarios nobody could read, and unable to say
# which of the remaining gaps is real.
#
# The parse happens ONCE per fixture and both readers above take the parsed
# document. Reading each file twice doubled the surface a traceback could escape
# from and proved nothing extra.
BROKEN_FIXTURE_ADVICE = (
    "       Restore it from a clean lazily-spec checkout. If this is a perturbation",
    "       probe under LAZILY_SPEC_CONFORMANCE_DIR, the PROBE is broken, not this",
    "       binding: a truncated fixture perturbs nothing a runner can disagree with.",
    "       Flip an assertion VALUE instead, and restore the file from the scratch",
    "       copy taken before the edit.",
)

on_disk = {}
declaring = set()
broken = []
for root, _, files in os.walk(spec_dir):
    for name in sorted(files):
        if not name.endswith(".json"):
            continue
        full = os.path.join(root, name)
        key = os.path.relpath(full, spec_dir)
        try:
            with open(full, "r", encoding="utf-8") as handle:
                document = json.loads(handle.read())
        except (OSError, ValueError) as exc:
            broken.append((key, full, exc))
            continue
        found = ids_of(document)
        if found:
            on_disk[key] = found
        if declares_prose(document):
            declaring.add(key)

if broken:
    lines = []
    for key, full, exc in sorted(broken, key=lambda item: item[0]):
        lines.extend(diagnose(f"canonical fixture '{key}' ({full})", exc, BROKEN_FIXTURE_ADVICE))
    lines.append(
        f"scenario replay coverage FAILED: {len(broken)} unreadable fixture(s) under {spec_dir}"
    )
    die(lines)

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
min_scenarios = read_int(
    os.environ["MIN_SCENARIOS"],
    "MIN_SCENARIOS",
    ("       Pass the count this guard REPORTS — the floor is EXACT, never a margin.",),
)
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
python3 -c "$PY_DIAG
$SCENARIO_GUARD_PY" "$SPEC_DIR" "$MANIFEST" \
  ${KNOWN_UNREPLAYED_SCENARIOS[@]+"${KNOWN_UNREPLAYED_SCENARIOS[@]}"}
