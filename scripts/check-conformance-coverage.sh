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

SPEC_DIR="${LAZILY_SPEC_CONFORMANCE_DIR:-../lazily-spec/conformance}"
if [ ! -d "$SPEC_DIR" ]; then
  # The one remaining path out of this script that is not a measurement. It exists
  # for a working copy with no sibling checkout, where the alternative is a guard
  # that cannot be run at all. It is NOT acceptable where the corpus is supposed to
  # be there: a clone step that silently no-ops would turn every rung below into a
  # green that proves nothing (#lzguardsnotinci). CI sets LAZILY_CONFORMANCE_STRICT
  # so the skip becomes a failure there.
  if [ -n "${LAZILY_CONFORMANCE_STRICT:-}" ]; then
    echo "FAIL: canonical corpus not found at $SPEC_DIR, and LAZILY_CONFORMANCE_STRICT is set." >&2
    echo "      Skipping here would report OK while checking nothing. The corpus is" >&2
    echo "      expected to be present in this environment — fix the checkout, do not" >&2
    echo "      unset the flag." >&2
    exit 1
  fi
  echo "SKIP: canonical corpus not found at $SPEC_DIR (clone the lazily-spec sibling)" >&2
  exit 0
fi

# Fixtures deliberately not covered by this binding yet. Keep this explicit even
# while empty: any future entry is a reviewed finding, never a silent skip.
KNOWN_UNCOVERED=(
)

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

MANIFEST="${LAZILY_CONFORMANCE_MANIFEST:-build/conformance-fixtures-loaded.txt}"
TEST_DIRS=("tests")
EXTS=(".cs")

collect_sources() {
  for d in "${TEST_DIRS[@]}"; do
    [ -d "$d" ] || continue
    for e in "${EXTS[@]}"; do
      find "$d" -type f -name "*$e" -print0
    done
  done
}

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

if [ "$missing" -gt 0 ]; then
  echo "conformance coverage FAILED: $missing problem(s)" >&2
  exit 1
fi

echo "conformance coverage OK: $covered/$total canonical fixtures OPENED by the suite" \
     "(${#KNOWN_UNCOVERED[@]} listed as known-uncovered; runtime manifest — these bytes were really read)"

# -- Per-scenario replay accounting (#lzscenariocoverage) ---------------------
#
# The rung below the fixture check above. The manifest carries two kinds of line:
# a bare `corpus/fixture.json` means the file was OPENED, a `corpus/fixture.json`
# + TAB + id line means that SCENARIO was reached at the point of replay. The
# scenario ids on disk are read HERE, independently of the runner, so a runner
# that resolves an id wrongly shows up as a mismatch rather than agreeing with
# itself.
#
# Id resolution is the fixed order every binding uses: `id`, else `name`, else the
# positional index spelled `#<n>`. Every id that fell back to a position is
# REPORTED below rather than silently accepted — the fallback exists so this guard
# is not blocked on a shared-corpus edit, and that visibility is what makes the
# corpus gap fixable upstream later.
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

opened = set()
replayed = set()
with open(manifest_path, encoding="utf-8") as handle:
    for line in handle:
        line = line.rstrip("\n")
        if not line:
            continue
        if "\t" in line:
            fixture, scenario = line.split("\t", 1)
            replayed.add((fixture, scenario))
        else:
            opened.add(line)


def ids_of(path):
    """`id`, else `name`, else the positional index — the fixed resolution order."""
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    scenarios = document.get("scenarios") if isinstance(document, dict) else None
    if not isinstance(scenarios, list):
        return []
    resolved = []
    for index, scenario in enumerate(scenarios):
        if isinstance(scenario, dict):
            if isinstance(scenario.get("id"), str):
                resolved.append((scenario["id"], False))
                continue
            if isinstance(scenario.get("name"), str):
                resolved.append((scenario["name"], False))
                continue
        resolved.append((f"#{index}", True))
    return resolved


on_disk = {}
for root, _, files in os.walk(spec_dir):
    for name in sorted(files):
        if not name.endswith(".json"):
            continue
        full = os.path.join(root, name)
        key = os.path.relpath(full, spec_dir)
        found = ids_of(full)
        if found:
            on_disk[key] = found

errors = list(excuse_errors)
positional = []
fixtures_checked = 0
scenarios_total = 0
scenarios_replayed = 0

for fixture in sorted(on_disk):
    found = on_disk[fixture]
    positional.extend(f"{fixture}#{index}" for index, (_, fell_back) in enumerate(found) if fell_back)
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

if positional:
    print(
        f"note: {len(positional)} scenario id(s) fell back to a positional #<n> — the "
        "fixture carries neither an `id` nor a `name` for them, so the ledger cannot say "
        "WHICH scenario went missing if one ever does. Fix upstream in lazily-spec, not "
        "here: " + ", ".join(positional)
    )

if errors:
    for error in errors:
        print(error, file=sys.stderr)
    print(f"scenario replay coverage FAILED: {len(errors)} problem(s)", file=sys.stderr)
    sys.exit(1)

print(
    f"scenario replay coverage OK: {scenarios_replayed}/{scenarios_total} scenarios across "
    f"{fixtures_checked} opened fixtures REPLAYED "
    f"({len(excuses)} listed as known-unreplayed; runtime ledger — these scenarios were "
    "really reached)"
)
PY
)"

python3 -c "$SCENARIO_GUARD_PY" "$SPEC_DIR" "$MANIFEST" \
  ${KNOWN_UNREPLAYED_SCENARIOS[@]+"${KNOWN_UNREPLAYED_SCENARIOS[@]}"}
