#!/usr/bin/env python3
"""Run the Core fixture consumer and emit its observed qualification report."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


BINDING_ID = "csharp"


def require_env(name: str) -> Path:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"{name} is required")
    return Path(value).resolve()


def run(argv: list[str], *, env: dict[str, str]) -> None:
    completed = subprocess.run(argv, env=env, text=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"{' '.join(argv)} exited {completed.returncode}")


def run_negative_control(argv: list[str], *, env: dict[str, str]) -> dict[str, object]:
    completed = subprocess.run(
        argv,
        env=env,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )
    if completed.returncode == 0:
        raise RuntimeError("mutated assertion was accepted by the reactive-graph runner")
    output = completed.stdout or ""
    return {
        "status": "rejected",
        "exit_code": completed.returncode,
        "output_excerpt": output[-1000:],
    }


def pointer_token(value: str) -> str:
    return value.replace("~", "~0").replace("/", "~1")


def route_pattern(pointer: str) -> re.Pattern[str]:
    parts = pointer.removeprefix("/").split("/")
    pattern = ""
    for part in parts:
        if part == "*":
            pattern += r"\[\d+\]"
        else:
            if pattern:
                pattern += r"\."
            pattern += re.escape(part.replace("~1", "/").replace("~0", "~"))
    return re.compile(r"^" + pattern + r"$")


def pointer_matches(document: object, pointer: str) -> list[tuple[object, str]]:
    matches: list[tuple[object, str]] = [(document, "")]
    for encoded in pointer.removeprefix("/").split("/"):
        token = encoded.replace("~1", "/").replace("~0", "~")
        expanded: list[tuple[object, str]] = []
        for value, path in matches:
            if token == "*" and isinstance(value, list):
                expanded.extend((item, f"{path}/{index}") for index, item in enumerate(value))
            elif isinstance(value, dict) and token in value:
                expanded.append((value[token], f"{path}/{pointer_token(token)}"))
        matches = expanded
    return matches


def mutate_assertion(value: object, path: str) -> tuple[object, dict[str, object]] | None:
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "note":
                continue
            result = mutate_assertion(child, f"{path}/{pointer_token(key)}")
            if result is not None:
                value[key], detail = result
                return value, detail
        return None
    if isinstance(value, list):
        for index, child in enumerate(value):
            result = mutate_assertion(child, f"{path}/{index}")
            if result is not None:
                value[index], detail = result
                return value, detail
        return None
    if isinstance(value, bool):
        replacement: object = not value
    elif isinstance(value, int):
        replacement = value + 1_000_003
    elif isinstance(value, float):
        replacement = value + 1_000_003.0
    elif isinstance(value, str):
        replacement = value + "__negative_control__"
    elif value is None:
        replacement = "__negative_control__"
    else:
        return None
    return replacement, {"json_pointer": path, "before": value, "after": replacement}


def reject_mutated_assertion(
    *,
    root: Path,
    spec_root: Path,
    build: Path,
    fixture_path: str,
    route: str,
    ordinal: int,
) -> dict[str, object]:
    with tempfile.TemporaryDirectory(prefix="core-mutant-", dir=build) as temporary:
        mutation_root = Path(temporary) / "conformance"
        shutil.copytree(spec_root / "conformance", mutation_root)
        fixture_file = mutation_root / fixture_path
        document = json.loads(fixture_file.read_text(encoding="utf-8"))
        matches = pointer_matches(document, route)
        if not matches:
            raise RuntimeError(f"{fixture_path}#{route}: mutation route did not resolve")
        assertion, assertion_path = matches[0]
        mutation = mutate_assertion(assertion, assertion_path)
        if mutation is None:
            raise RuntimeError(f"{fixture_path}#{route}: no mutable assertion leaf")
        _, detail = mutation
        fixture_file.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        mutant_manifest = build / f"core-mutant-{ordinal}.txt"
        mutant_manifest.unlink(missing_ok=True)
        mutant_env = os.environ.copy()
        mutant_env.update({
            "LAZILY_SPEC_CONFORMANCE_DIR": str(mutation_root),
            "LAZILY_CONFORMANCE_MANIFEST": str(mutant_manifest),
            "LAZILY_CONFORMANCE_RUN_ID": f"core-mutant-{os.getpid()}-{ordinal}",
        })
        rejection = run_negative_control([
            "dotnet", "test", "tests/Lazily.Tests/Lazily.Tests.csproj", "--nologo",
            "--no-build", "--filter", "FullyQualifiedName~ReactiveGraphConformanceTests",
        ], env=mutant_env)
        return {"mutation": detail, **rejection}


def parse_manifest(path: Path) -> tuple[set[str], set[tuple[str, str]], dict[str, list[tuple[str, str]]], set[str]]:
    opened: set[str] = set()
    replayed: set[tuple[str, str]] = set()
    declared: dict[str, list[tuple[str, str]]] = {}
    bound: set[str] = set()
    for raw in path.read_text(encoding="utf-8").splitlines():
        fields = raw.split("\t")
        if len(fields) == 1 and fields[0] and not fields[0].startswith("#"):
            opened.add(fields[0])
        elif len(fields) == 2 and fields[0].startswith("reactive-graph/"):
            replayed.add((fields[0], fields[1]))
        elif len(fields) == 4 and fields[0] == "blocks-declared":
            declared.setdefault(fields[1], []).append((fields[3], fields[2]))
        elif len(fields) == 2 and fields[0] == "blocks-bound":
            bound.add(fields[1])
    return opened, replayed, declared, bound


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    profile_path = require_env("LAZILY_CORE_PROFILE_PATH")
    spec_root = require_env("LAZILY_CORE_SPEC_ROOT")
    reports_root = require_env("LAZILY_CORE_REPORTS_ROOT")
    report_path = require_env("LAZILY_CORE_REPORT_PATH")
    profile = json.loads(profile_path.read_text(encoding="utf-8"))
    binding = next(item for item in profile["bindings"] if item["id"] == BINDING_ID)
    required = {item["path"]: item for item in profile["required_fixtures"]}
    routes_document = json.loads((spec_root / "schemas/assertion-blocks.json").read_text(encoding="utf-8"))
    routes: dict[str, list[str]] = {path: [] for path in binding["required_fixtures"]}
    for route in routes_document["x-fixture-routes"]:
        if route["fixture"] in routes:
            routes[route["fixture"]].append(route["pointer"])

    build = root / "build/core-conformance"
    manifest = build / "core-manifest.txt"
    build.mkdir(parents=True, exist_ok=True)
    manifest.unlink(missing_ok=True)
    test_env = os.environ.copy()
    test_env.update({
        "LAZILY_SPEC_CONFORMANCE_DIR": str(spec_root / "conformance"),
        "LAZILY_CONFORMANCE_MANIFEST": str(manifest),
        "LAZILY_CONFORMANCE_RUN_ID": f"core-report-{os.getpid()}",
    })
    run([
        "dotnet", "test", "tests/Lazily.Tests/Lazily.Tests.csproj", "--nologo",
        "--filter", "FullyQualifiedName~ReactiveGraphConformanceTests",
    ], env=test_env)
    if not manifest.is_file() or manifest.stat().st_size == 0:
        raise RuntimeError("reactive-graph runner produced no runtime manifest")

    opened, replayed, declared, bound = parse_manifest(manifest)
    evidence: dict[str, object] = {
        "schema_version": 1,
        "binding_id": BINDING_ID,
        "producer": "tests/Lazily.Tests/ReactiveGraphConformanceTests.cs",
        "execution_flavors": ["single_threaded", "thread_safe", "async"],
        "fixtures": {},
    }
    observed_fixtures: list[dict[str, object]] = []
    mutation_ordinal = 0
    for fixture_path in binding["required_fixtures"]:
        definition = required[fixture_path]
        if fixture_path not in opened:
            raise RuntimeError(f"required fixture was not opened: {fixture_path}")
        executed_scenarios = sorted(
            scenario for fixture, scenario in replayed if fixture == fixture_path
        )
        if set(executed_scenarios) != set(definition["scenario_ids"]):
            raise RuntimeError(
                f"{fixture_path}: executed scenarios {executed_scenarios!r} do not match profile "
                f"{definition['scenario_ids']!r}"
            )
        blocks: list[dict[str, object]] = []
        fixture_evidence: dict[str, object] = {"routes": {}}
        for route in routes[fixture_path]:
            sites = [(site, digest) for site, digest in declared.get(fixture_path, []) if route_pattern(route).match(site)]
            if not sites:
                raise RuntimeError(f"{fixture_path}#{route}: runtime manifest declared no matching assertion sites")
            missing = sorted(site for site, digest in sites if digest not in bound)
            if missing:
                raise RuntimeError(f"{fixture_path}#{route}: assertion sites were not consumed: {missing}")
            fixture_evidence["routes"][route] = {
                "status": "mutation-killed",
                "consumed_sites": sorted(site for site, _ in sites),
                "bound_digests": sorted({digest for _, digest in sites}),
                "negative_control": reject_mutated_assertion(
                    root=root,
                    spec_root=spec_root,
                    build=build,
                    fixture_path=fixture_path,
                    route=route,
                    ordinal=mutation_ordinal,
                ),
            }
            mutation_ordinal += 1
            reference = (
                f"{BINDING_ID}/assertion-evidence.json#/fixtures/{pointer_token(fixture_path)}"
                f"/routes/{pointer_token(route)}"
            )
            blocks.append({
                "id": route,
                "consumed": True,
                "discriminability_evidence": reference,
            })
        evidence["fixtures"][fixture_path] = fixture_evidence
        observed_fixtures.append({
            "path": fixture_path,
            "scenario_ids": executed_scenarios,
            "proof_classes": definition["proof_classes"],
            "assertion_blocks": blocks,
            "status": "passed",
        })

    evidence_dir = reports_root / BINDING_ID
    evidence_dir.mkdir(parents=True, exist_ok=True)
    (evidence_dir / "assertion-evidence.json").write_text(
        json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    report = {
        "schema_version": 1,
        "profile_id": profile["profile_id"],
        "binding_id": BINDING_ID,
        "fixture_corpus_digest": profile["fixture_corpus"]["digest"],
        "fixtures": observed_fixtures,
        "runtime_skips": [],
        "fixtureless_claims": [{
            "id": "c_abi_ffi_boundary",
            "proof_classes": ["native_artifact_consumer"],
            "passed": True,
            "evidence": f"{BINDING_ID}/c-abi-ffi-boundary.json",
        }],
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"core report FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
