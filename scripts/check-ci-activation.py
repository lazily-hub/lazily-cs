#!/usr/bin/env python3
"""Pin counted workflow and gate-job activation (#verifyworkflowactually)."""

from __future__ import annotations

import argparse
import fnmatch
import re
import shlex
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path


class CheckError(RuntimeError):
    pass


@dataclass
class Step:
    name: str
    run: str


@dataclass
class Job:
    activation: dict[str, str] = field(default_factory=dict)
    matrix: dict[str, list[str]] = field(default_factory=dict)
    steps: list[Step] = field(default_factory=list)


@dataclass
class Workflow:
    triggers: set[str] = field(default_factory=set)
    filters: set[tuple[str, str, str]] = field(default_factory=set)
    jobs: dict[str, Job] = field(default_factory=dict)


def unquote(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
        return value[1:-1]
    return value


def key_value(text: str) -> tuple[str, str]:
    match = re.match(r"^([^:]+):(.*)$", text)
    if not match:
        raise CheckError(f"unsupported YAML mapping entry: {text!r}")
    value = match.group(2).strip()
    quote = ""
    for index, char in enumerate(value):
        if char in "'\"":
            quote = "" if quote == char else (char if not quote else quote)
        elif char == "#" and not quote and (index == 0 or value[index - 1].isspace()):
            value = value[:index].rstrip()
            break
    return unquote(match.group(1)), value


def list_value(value: str) -> list[str]:
    if value.startswith("[") and value.endswith("]"):
        return [unquote(part) for part in value[1:-1].split(",") if part.strip()]
    return [unquote(value)] if value else []


def useful_lines(path: Path) -> list[tuple[int, int, str]]:
    rows = []
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        prefix = raw[: len(raw) - len(raw.lstrip())]
        if "\t" in prefix:
            raise CheckError(f"{path}:{number}: tabs in YAML indentation")
        text = raw.strip()
        if text and not text.startswith("#"):
            rows.append((number, len(raw) - len(raw.lstrip(" ")), text))
    return rows


def child_block(rows, start: int, parent_indent: int):
    end = start
    while end < len(rows) and rows[end][1] > parent_indent:
        end += 1
    return rows[start:end], end


def parse_workflow(path: Path) -> Workflow:
    rows = useful_lines(path)
    result = Workflow()
    roots = [(i, text) for i, (_, indent, text) in enumerate(rows) if indent == 0]
    on_entry = next(
        ((i, text) for i, text in roots if unquote(text.split(":", 1)[0]) in {"on", "true", "True"}),
        None,
    )
    jobs_entry = next(((i, text) for i, text in roots if text.startswith("jobs:")), None)
    if on_entry is None or jobs_entry is None:
        raise CheckError(f"{path}: missing readable top-level on: or jobs:")

    on_index, on_text = on_entry
    _, on_value = key_value(on_text)
    if on_value:
        result.triggers.update(list_value(on_value))
    else:
        on_rows, _ = child_block(rows, on_index + 1, 0)
        i = 0
        while i < len(on_rows):
            number, indent, text = on_rows[i]
            if indent != 2:
                raise CheckError(f"{path}:{number}: unsupported on: shape")
            trigger, value = key_value(text)
            if trigger in result.triggers or value:
                raise CheckError(f"{path}:{number}: duplicate or inline trigger")
            result.triggers.add(trigger)
            block, next_i = child_block(on_rows, i + 1, indent)
            j = 0
            seen = set()
            while j < len(block):
                f_number, f_indent, f_text = block[j]
                if f_indent != 4:
                    raise CheckError(f"{path}:{f_number}: unsupported trigger-filter shape")
                f_key, f_value = key_value(f_text)
                if f_key in seen:
                    raise CheckError(f"{path}:{f_number}: duplicate filter {trigger}.{f_key}")
                seen.add(f_key)
                nested, next_j = child_block(block, j + 1, f_indent)
                if f_value:
                    serialized = ",".join(list_value(f_value))
                elif nested and all(row[2].startswith("- ") for row in nested):
                    serialized = ",".join(unquote(row[2][2:]) for row in nested)
                elif nested:
                    serialized = "<mapping>"
                else:
                    serialized = ""
                result.filters.add((trigger, f_key, serialized))
                j = next_j
            i = next_i

    jobs_index, _ = jobs_entry
    job_rows, _ = child_block(rows, jobs_index + 1, 0)
    i = 0
    while i < len(job_rows):
        number, indent, text = job_rows[i]
        if indent != 2:
            raise CheckError(f"{path}:{number}: unsupported jobs: shape")
        job_id, inline = key_value(text)
        if inline or job_id in result.jobs:
            raise CheckError(f"{path}:{number}: duplicate or inline job {job_id}")
        job = Job()
        result.jobs[job_id] = job
        block, next_i = child_block(job_rows, i + 1, indent)
        own = {}
        j = 0
        while j < len(block):
            own_number, own_indent, own_text = block[j]
            if own_indent != 4:
                raise CheckError(f"{path}:{own_number}: unsupported job shape")
            own_key, own_value = key_value(own_text)
            nested, next_j = child_block(block, j + 1, own_indent)
            if own_key in own:
                raise CheckError(f"{path}:{own_number}: duplicate job key {job_id}.{own_key}")
            own[own_key] = (own_value, nested)
            j = next_j

        for name in ("if", "continue-on-error", "needs"):
            if name not in own:
                continue
            value, nested = own[name]
            if nested:
                if name == "needs" and all(row[2].startswith("- ") for row in nested):
                    value = ",".join(unquote(row[2][2:]) for row in nested)
                else:
                    raise CheckError(f"{path}: multiline job-level {name} is unsupported")
            job.activation[name] = unquote(value)

        if "strategy" in own:
            strategy_value, strategy_rows = own["strategy"]
            if strategy_value:
                raise CheckError(f"{path}: inline strategy is unsupported")
            matrix_at = next(
                (k for k, row in enumerate(strategy_rows) if row[1] == 6 and row[2].startswith("matrix:")),
                None,
            )
            if matrix_at is not None:
                _, matrix_value = key_value(strategy_rows[matrix_at][2])
                if matrix_value:
                    raise CheckError(f"{path}: inline matrix is unsupported")
                matrix_rows, _ = child_block(strategy_rows, matrix_at + 1, 6)
                k = 0
                while k < len(matrix_rows):
                    axis_number, axis_indent, axis_text = matrix_rows[k]
                    if axis_indent != 8:
                        raise CheckError(f"{path}:{axis_number}: unsupported matrix shape")
                    axis, axis_value = key_value(axis_text)
                    if axis in {"include", "exclude"} or axis in job.matrix:
                        raise CheckError(f"{path}:{axis_number}: unsupported or duplicate matrix axis {axis}")
                    nested, next_k = child_block(matrix_rows, k + 1, axis_indent)
                    if axis_value:
                        legs = list_value(axis_value)
                    elif nested and all(row[2].startswith("- ") for row in nested):
                        legs = [unquote(row[2][2:]) for row in nested]
                    else:
                        raise CheckError(f"{path}:{axis_number}: unsupported matrix values")
                    if not legs:
                        raise CheckError(f"{path}:{axis_number}: empty matrix axis {axis}")
                    job.matrix[axis] = legs
                    k = next_k

        if "steps" in own:
            steps_value, step_rows = own["steps"]
            if steps_value:
                raise CheckError(f"{path}: inline steps are unsupported")
            k = 0
            current_name = ""
            while k < len(step_rows):
                step_number, step_indent, step_text = step_rows[k]
                if step_indent == 6 and step_text.startswith("- "):
                    current_name = ""
                    first = step_text[2:]
                    if first.startswith("name:"):
                        _, current_name = key_value(first)
                        current_name = unquote(current_name)
                    k += 1
                    continue
                if step_indent == 8 and step_text.startswith("name:"):
                    _, current_name = key_value(step_text)
                    current_name = unquote(current_name)
                    k += 1
                    continue
                if step_indent == 8 and step_text.startswith("run:"):
                    _, run_value = key_value(step_text)
                    run_block, next_k = child_block(step_rows, k + 1, step_indent)
                    if run_value in {"|", ">", "|-", ">-", "|+", ">+"}:
                        run = "\n".join(row[2] for row in run_block)
                    elif run_value:
                        run = run_value
                    else:
                        raise CheckError(f"{path}:{step_number}: empty run:")
                    job.steps.append(Step(current_name, run))
                    k = next_k
                    continue
                k += 1
        i = next_i

    if not result.triggers or not result.jobs:
        raise CheckError(f"{path}: parser produced an empty trigger or job set")
    return result


def parse_array(guard: Path, names: tuple[str, ...]) -> list[str]:
    source = guard.read_text(encoding="utf-8")
    for name in names:
        if re.search(rf"(?m)^\s*{re.escape(name)}=\(\s*\)\s*$", source):
            return []
        match = re.search(rf"(?ms)^\s*{re.escape(name)}=\(\s*\n(.*?)^\s*\)\s*$", source)
        if not match:
            continue
        values = []
        for raw in match.group(1).splitlines():
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            try:
                fields = shlex.split(line)
            except ValueError as exc:
                raise CheckError(f"{guard}: cannot parse {name}: {exc}") from exc
            if len(fields) != 1:
                raise CheckError(f"{guard}: malformed {name} row: {raw!r}")
            values.append(fields[0])
        return values
    raise CheckError(f"{guard}: none of {', '.join(names)} exists")


def parse_config(path: Path):
    rows = {}
    workflows = []
    allowed = {
        "workflow", "trigger", "filter", "gate-job", "activation", "matrix",
        "required-trigger", "required-branch",
    }
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = tuple(line.split("|"))
        if parts[0] not in allowed:
            raise CheckError(f"{path}:{number}: unknown row kind {parts[0]!r}")
        rows.setdefault(parts[0], []).append(parts[1:])
        if parts[0] == "workflow":
            if len(parts) != 2:
                raise CheckError(f"{path}:{number}: malformed workflow row")
            workflows.append(parts[1])
    if not workflows:
        raise CheckError(f"{path}: no counted workflow")
    if len(set(workflows)) != len(workflows):
        raise CheckError(f"{path}: duplicate counted workflow")
    return rows, workflows


def pins(rows, kind: str, width: int):
    values = rows.get(kind, [])
    if any(len(value) != width for value in values):
        raise CheckError(f"{kind} pin has the wrong field count")
    if len(set(values)) != len(values):
        raise CheckError(f"{kind} pin contains duplicates")
    return set(values)


def step_pin_names(guard: Path) -> list[str]:
    values = parse_array(guard, ("EXPECTED_GATE_STEPS", "EXPECTED_STEP_FOR_TARGET"))
    names = []
    for value in values:
        if "|" in value:
            names.append(value.rsplit("|", 1)[1].strip())
        else:
            fields = value.split(None, 1)
            if len(fields) != 2:
                raise CheckError(f"{guard}: gate-step row has no name: {value!r}")
            names.append(fields[1].strip())
    if not names:
        raise CheckError(f"{guard}: empty gate-step pin")
    return names


def make_pin_targets(guard: Path) -> list[str]:
    return parse_array(guard, ("EXPECTED_MAKE_INVOKED_TARGETS", "EXPECTED_MAKE_INVOKED"))


def match_make(run: str, target: str) -> bool:
    active = "\n".join(line for line in run.splitlines() if not line.lstrip().startswith("#"))
    return re.search(
        rf"(?m)(?:^|[;&|()\s])make\s+(?:-[^\s]+\s+)*{re.escape(target)}(?:$|[;&|()\s])",
        active,
    ) is not None


def activation(job: Job) -> str:
    return ";".join(f"{key}={job.activation.get(key, '')}" for key in ("continue-on-error", "if", "needs"))


def matrix(job: Job) -> str:
    return ";".join(f"{axis}={','.join(sorted(job.matrix[axis]))}" for axis in sorted(job.matrix))


def check(root: Path, config: Path, guard: Path, quiet: bool = False) -> None:
    rows, workflow_paths = parse_config(config)
    workflows = {name: parse_workflow(root / name) for name in workflow_paths}
    found_triggers = {(wf, event) for wf, data in workflows.items() for event in data.triggers}
    found_filters = {
        (wf, event, f"{key}={value}")
        for wf, data in workflows.items()
        for event, key, value in data.filters
    }
    expected_triggers = pins(rows, "trigger", 2)
    expected_filters = pins(rows, "filter", 3)
    if found_triggers != expected_triggers:
        raise CheckError(f"EXPECTED_TRIGGERS mismatch: pinned={sorted(expected_triggers)!r} found={sorted(found_triggers)!r}")
    if found_filters != expected_filters:
        raise CheckError(f"EXPECTED_TRIGGER_FILTERS mismatch: pinned={sorted(expected_filters)!r} found={sorted(found_filters)!r}")

    required = {item[0] for item in pins(rows, "required-trigger", 1)}
    branch_rows = pins(rows, "required-branch", 1)
    if not required or len(branch_rows) != 1:
        raise CheckError("required-trigger and required-branch floors must be non-empty")
    branch = next(iter(branch_rows))[0]
    for wf, data in workflows.items():
        for event in required:
            if event not in data.triggers:
                raise CheckError(f"{wf}: required trigger {event!r} is absent")
            filters = {key: value for trig, key, value in data.filters if trig == event}
            forbidden = sorted(set(filters) & {"paths", "paths-ignore", "branches-ignore", "tags", "tags-ignore"})
            if forbidden:
                raise CheckError(f"{wf}: {event} has forbidden gating filter(s): {forbidden}")
            if "branches" in filters:
                patterns = filters["branches"].split(",")
                if any(p.startswith("!") for p in patterns) or not any(fnmatch.fnmatchcase(branch, p) for p in patterns):
                    raise CheckError(f"{wf}: {event} branches do not include {branch!r}")

    located_jobs = set()
    for name in step_pin_names(guard):
        matches = [
            (wf, job_id)
            for wf, data in workflows.items()
            for job_id, job in data.jobs.items()
            for step in job.steps
            if step.name == name
        ]
        if len(matches) != 1:
            raise CheckError(f"gate step {name!r} resolves to {len(matches)} jobs")
        located_jobs.add(matches[0])

    for target in make_pin_targets(guard):
        matches = {
            (wf, job_id)
            for wf, data in workflows.items()
            for job_id, job in data.jobs.items()
            for step in job.steps
            if match_make(step.run, target)
        }
        if len(matches) != 1:
            raise CheckError(f"make-invoked gate {target!r} resolves to {len(matches)} jobs")
        located_jobs.update(matches)

    expected_jobs = pins(rows, "gate-job", 2)
    if located_jobs != expected_jobs:
        raise CheckError(f"EXPECTED_GATE_JOBS mismatch: pinned={sorted(expected_jobs)!r} found={sorted(located_jobs)!r}")
    found_activation = {(wf, job_id, activation(workflows[wf].jobs[job_id])) for wf, job_id in expected_jobs}
    found_matrix = {(wf, job_id, matrix(workflows[wf].jobs[job_id])) for wf, job_id in expected_jobs}
    expected_activation = pins(rows, "activation", 3)
    expected_matrix = pins(rows, "matrix", 3)
    if {row[:2] for row in expected_activation} != expected_jobs or {row[:2] for row in expected_matrix} != expected_jobs:
        raise CheckError("activation/matrix key sets must equal EXPECTED_GATE_JOBS")
    if found_activation != expected_activation:
        raise CheckError(f"EXPECTED_JOB_ACTIVATION mismatch: pinned={sorted(expected_activation)!r} found={sorted(found_activation)!r}")
    if found_matrix != expected_matrix:
        raise CheckError(f"EXPECTED_JOB_MATRIX mismatch: pinned={sorted(expected_matrix)!r} found={sorted(found_matrix)!r}")

    advisory_re = re.compile(r"^\$\{\{\s*matrix\.([A-Za-z0-9_-]+)\s*==\s*(['\"])(.*?)\2\s*\}\}$")
    false_expr = "$" + "{{ false }}"
    true_expr = "$" + "{{ true }}"
    for wf, job_id in expected_jobs:
        job = workflows[wf].jobs[job_id]
        condition = job.activation.get("if", "").strip()
        discard = job.activation.get("continue-on-error", "").strip()
        if condition.lower() in {"false", false_expr}:
            raise CheckError(f"{wf}:{job_id}: gate job has an always-false if")
        if discard.lower() in {"true", true_expr}:
            raise CheckError(f"{wf}:{job_id}: gate job discards every failure")
        if discard:
            match = advisory_re.fullmatch(discard)
            if not match:
                raise CheckError(f"{wf}:{job_id}: cannot prove a blocking matrix leg for {discard!r}")
            axis, _, leg = match.groups()
            if axis not in job.matrix or leg not in job.matrix[axis]:
                raise CheckError(f"{wf}:{job_id}: advisory predicate names no matrix leg")
            total = 1
            for legs in job.matrix.values():
                total *= len(legs)
            if total - total // len(job.matrix[axis]) < 1:
                raise CheckError(f"{wf}:{job_id}: every matrix leg is advisory")

    if not quiet:
        print(
            f"check-ci-reach: activated — {len(found_triggers)} trigger(s), "
            f"{len(found_filters)} filter(s), {len(expected_jobs)} gate job(s), "
            f"exact activation/matrix pins; {','.join(sorted(required))}/{branch} floors"
        )


def self_test() -> None:
    expression = "$" + "{{ matrix.version == 'master' }}"
    with tempfile.TemporaryDirectory(prefix="ci-activation-") as temp:
        root = Path(temp)
        (root / ".github/workflows").mkdir(parents=True)
        (root / "scripts").mkdir()
        guard = root / "scripts/check-ci-reach.sh"
        guard.write_text('EXPECTED_GATE_STEPS=(\n  "gate|Gate"\n)\nEXPECTED_MAKE_INVOKED_TARGETS=(\n)\n', encoding="utf-8")
        config = root / "scripts/ci-activation.conf"
        healthy_config = f"""workflow|.github/workflows/ci.yml
trigger|.github/workflows/ci.yml|push
trigger|.github/workflows/ci.yml|pull_request
filter|.github/workflows/ci.yml|push|branches=main
filter|.github/workflows/ci.yml|pull_request|branches=main
gate-job|.github/workflows/ci.yml|test
activation|.github/workflows/ci.yml|test|continue-on-error={expression};if=;needs=
matrix|.github/workflows/ci.yml|test|version=master,stable
required-trigger|push
required-trigger|pull_request
required-branch|main
"""
        healthy_workflow = f"""name: CI
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
jobs:
  test:
    runs-on: ubuntu-latest
    continue-on-error: {expression}
    strategy:
      matrix:
        version: [stable, master]
    steps:
      - name: Gate
        run: echo gate
"""
        workflow = root / ".github/workflows/ci.yml"
        config.write_text(healthy_config, encoding="utf-8")
        workflow.write_text(healthy_workflow, encoding="utf-8")
        check(root, config, guard, quiet=True)
        attacks = {
            "dispatch-only": (healthy_workflow.replace("  push:\n    branches: [main]\n  pull_request:\n    branches: [main]\n", "  workflow_dispatch:\n"), healthy_config),
            "path-filter": (healthy_workflow.replace("    branches: [main]\n", "    branches: [main]\n    paths: [src/**]\n", 1), healthy_config),
            "wrong-branch": (healthy_workflow.replace("branches: [main]", "branches: [release]"), healthy_config),
            "if-false-repinned": (healthy_workflow.replace("    runs-on:", "    if: false\n    runs-on:"), healthy_config.replace("if=;needs=", "if=false;needs=")),
            "discard-all-repinned": (healthy_workflow.replace(expression, "true"), healthy_config.replace(expression, "true")),
            "advisory-only-repinned": (healthy_workflow.replace("[stable, master]", "[master]"), healthy_config.replace("version=master,stable", "version=master")),
            "moved-gate": (healthy_workflow.replace("  test:", "  moved:"), healthy_config),
            "unsupported-on": (healthy_workflow.replace("  push:\n", "  - push\n", 1), healthy_config),
        }
        for name, (workflow_text, config_text) in attacks.items():
            workflow.write_text(workflow_text, encoding="utf-8")
            config.write_text(config_text, encoding="utf-8")
            try:
                check(root, config, guard, quiet=True)
            except CheckError:
                continue
            raise CheckError(f"self-test attack stayed green: {name}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path)
    parser.add_argument("--guard", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
        else:
            if args.config is None or args.guard is None:
                parser.error("--config and --guard are required")
            check(Path.cwd(), args.config, args.guard)
    except (CheckError, OSError) as exc:
        print(f"check-ci-reach: activation failure: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
