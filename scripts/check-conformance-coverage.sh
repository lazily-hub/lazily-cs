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
  echo "SKIP: canonical corpus not found at $SPEC_DIR (clone the lazily-spec sibling)" >&2
  exit 0
fi

# Fixtures deliberately not covered by this binding yet. Each entry is a claim that
# someone looked; shrinking this list is the work. Adding to it silently is how the
# guard rots, so keep a reason with any new entry.
KNOWN_UNCOVERED=(
  "agent-doc/delta_agent_doc_state.json"
  "agent-doc/snapshot_agent_doc_state.json"
  "arena_blob.json"
  "collections/mergecell_algebra.json"
  "collections/seqcrdt_convergence.json"
  "collections/textcrdt_convergence.json"
  "collections/textcrdt_delta_sync.json"
  "coordination/leader.json"
  "coordination/lease.json"
  "coordination/lock.json"
"coordination/quorum.json"
"coordination/semaphore.json"
"crdt-tree/algebra.json"
"distributed/anti_entropy_converge.json"
"lossless-tree/concurrent_conflict_preserves_text.json"
  "lossless-tree/concurrent_insert_same_parent.json"
  "lossless-tree/concurrent_reorder_and_leaf_edit.json"
  "lossless-tree/exact_roundtrip.json"
  "lossless-tree/invalid_source_roundtrip.json"
  "lossless-tree/non_contiguous_anti_entropy.json"
  "lossless-tree/one_leaf_edit_delta.json"
  "lossless-tree/split_merge.json"
  "lossless-tree/token_trivia_preservation.json"
  "membership/membership_lifecycle.json"
  "message-passing/accepted_then_applied_receipt.json"
  "message-passing/cancel_preempts_nonterminal.json"
  "message-passing/editor_route_submit.json"
  "message-passing/reconnect_command_projection.json"
  "message-passing/rpc_call_waits_for_terminal.json"
  "message-passing/stale_generation_ignored.json"
  "message-passing/sync_tmux_layout_submit.json"
  "message-passing/terminal_conflict_fail_closed.json"
  "presence/awareness.json"
  "presence/ephemeral.json"
  "presence/presence.json"
  "rateshape/debounce.json"
  "rateshape/probabilistic_sample.json"
  "rateshape/sample_count.json"
  "rateshape/sample_time.json"
  "rateshape/throttle_leading.json"
  "rateshape/throttle_trailing.json"
  "receipts/causal_receipts.json"
  "reliable-sync/coalesce_bounds_outbox.json"
  "reliable-sync/idempotent_redelivery.json"
  "reliable-sync/liveness_lease_eviction.json"
  "reliable-sync/liveness_orset_lww.json"
  "reliable-sync/multi_epoch_delta.json"
  "reliable-sync/outbox_replay_after_crash.json"
  "reliable-sync/outbox_store_protocol.json"
  "reliable-sync/resync_gap_converge.json"
  "resilience/bulkhead.json"
  "resilience/circuit_breaker.json"
  "resilience/retry.json"
  "resilience/timeout.json"
  "service/discovery.json"
  "service/health.json"
  "service/readiness.json"
"service/service_registry.json"
"signaling/anti_spoof_session.json"
"signaling/frames.json"
"temporal/cron_pattern.json"
  "temporal/deadline_expiry.json"
  "temporal/interval_periodic.json"
  "temporal/timer_single_shot.json"
  "windowing/session.json"
  "windowing/sliding_count.json"
  "windowing/tumbling_count.json"
  "windowing/tumbling_time.json"
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
  for known in "${KNOWN_UNCOVERED[@]:-}"; do
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

# A stale allowlist is its own drift: an entry naming a fixture that no longer
# exists means the corpus moved and nobody updated the excuse.
for known in "${KNOWN_UNCOVERED[@]:-}"; do
  if [ ! -f "$SPEC_DIR/$known" ]; then
    echo "ERROR: KNOWN_UNCOVERED lists '$known', which is not in the canonical corpus." >&2
    missing=$((missing + 1))
  fi
done

if [ "$missing" -gt 0 ]; then
  echo "conformance coverage FAILED: $missing problem(s)" >&2
  exit 1
fi

echo "conformance coverage OK: $covered/$total canonical fixtures OPENED by the suite" \
     "(${#KNOWN_UNCOVERED[@]} listed as known-uncovered; runtime manifest — these bytes were really read)"
