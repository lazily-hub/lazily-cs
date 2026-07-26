#!/usr/bin/env bash
# Conformance-coverage guard (#portconformancecoverage).
#
# Fails the build when the canonical corpus in ../lazily-spec/conformance/ grows a
# fixture that no test in this repo even mentions. That is the drift this guard
# exists for: a fixture lands upstream, every binding stays green, and nobody
# learns that one of them is not replaying it.
#
# WHAT THIS CATCHES AND WHAT IT DOES NOT — read before trusting it.
#
# This is a STATIC guard. It greps the test sources for each canonical fixture's
# filename. So:
#   * absent   -> caught. A fixture no test names cannot be being replayed.
#   * present  -> NOT proof of replay. A test may name a fixture in a comment and
#                 hand-transcribe its contents, which is exactly the drift found in
#                 lazily-cpp's queue tests. Only a RUNTIME manifest proves the
#                 bytes were opened, which is what lazily-kt and lazily-cpp do via
#                 LAZILY_CONFORMANCE_MANIFEST.
#
# So a green run here means "no canonical fixture is unmentioned", not "every
# canonical fixture is replayed". Upgrading this binding to the runtime manifest is
# strictly better; this is the portable floor, not the ceiling.
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
  "collections/keyed_reconciliation_lis.json"
  "coordination/leader.json"
  "coordination/lease.json"
  "coordination/lock.json"
  "coordination/quorum.json"
  "coordination/semaphore.json"
  "delta_non_sequential.json"
  "delta_sequential.json"
  "delta_shared_blob.json"
  "delta_zero_copy_arrow.json"
  "distributed/anti_entropy_converge.json"
  "distributed/crdt_sync_frames.json"
  "familysync/materialize_on_ingest.json"
  "lossless-tree/concurrent_conflict_preserves_text.json"
  "lossless-tree/concurrent_insert_same_parent.json"
  "lossless-tree/concurrent_reorder_and_leaf_edit.json"
  "lossless-tree/exact_roundtrip.json"
  "lossless-tree/invalid_source_roundtrip.json"
  "lossless-tree/non_contiguous_anti_entropy.json"
  "lossless-tree/one_leaf_edit_delta.json"
  "lossless-tree/split_merge.json"
  "lossless-tree/token_trivia_preservation.json"
  "materialization/deferral_not_deallocation.json"
  "materialization/entry_kind_orthogonal_to_mode.json"
  "materialization/observational_transparency.json"
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
  "reactive-graph/churn_returns_to_baseline.json"
  "reactive-graph/cross_scope_teardown_hazard.json"
  "reactive-graph/disarm_disposes_nothing.json"
  "reactive-graph/disposal_does_not_run_surviving_effects.json"
  "reactive-graph/dispose_detaches_edges_both_directions.json"
  "reactive-graph/read_after_dispose_is_an_error.json"
  "reactive-graph/recycled_id_inherits_nothing.json"
  "reactive-graph/scope_teardown_equals_fold_of_disposals.json"
  "reactive-graph/scoping_bounds_teardown_not_visibility.json"
  "reactive-graph/teardown_runs_members_in_reverse_creation_order.json"
  "reactive-graph/transitive_invalidation_reaches_depth.json"
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
  "snapshot_minimal.json"
  "snapshot_multi_node.json"
  "snapshot_shared_blob.json"
  "statechart/entry_exit_actions.json"
  "statechart/flat_cycle.json"
  "statechart/guarded_door.json"
  "statechart/hierarchical_player.json"
  "statechart/history_deep.json"
  "statechart/history_shallow.json"
  "statechart/parallel_regions.json"
  "temporal/cron_pattern.json"
  "temporal/deadline_expiry.json"
  "temporal/interval_periodic.json"
  "temporal/timer_single_shot.json"
  "windowing/session.json"
  "windowing/sliding_count.json"
  "windowing/tumbling_count.json"
  "windowing/tumbling_time.json"
)

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

SOURCES="$(collect_sources | xargs -0 cat 2>/dev/null || true)"
if [ -z "$SOURCES" ]; then
  echo "FAIL: read no test sources from ${TEST_DIRS[*]}; this check would be vacuous" >&2
  exit 1
fi

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
  if grep -qF "$name" <<< "$SOURCES"; then
    covered=$((covered + 1))
    continue
  fi
  excused=0
  for known in "${KNOWN_UNCOVERED[@]:-}"; do
    if [ "$known" = "$fixture" ]; then excused=1; break; fi
  done
  if [ "$excused" -eq 0 ]; then
    echo "ERROR: canonical fixture '$fixture' exists but no test in this repo names it." >&2
    echo "       Write a runner that replays it, or add it to KNOWN_UNCOVERED with a reason." >&2
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

echo "conformance coverage OK: $covered/$total canonical fixtures named by tests" \
     "(${#KNOWN_UNCOVERED[@]} listed as known-uncovered; static check — naming is not replaying)"
