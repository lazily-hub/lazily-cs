#!/usr/bin/env python3
"""Build the NativeAOT library, compile its C consumer, and invoke it."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path


def main() -> None:
    evidence_raw = os.environ.get("LAZILY_CORE_EVIDENCE_PATH")
    if not evidence_raw:
        raise RuntimeError("LAZILY_CORE_EVIDENCE_PATH is required")
    evidence = Path(evidence_raw).resolve()
    root = Path(__file__).resolve().parents[1]
    completed = subprocess.run([str(root / "scripts/check-ffi.sh")], cwd=root, text=True, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"scripts/check-ffi.sh exited {completed.returncode}")
    library_name = "lazily_ffi.dylib" if sys.platform == "darwin" else "lazily_ffi.so"
    library = root / "build/native-aot" / library_name
    consumer = root / "build/native-aot/ffi-smoke"
    if not library.is_file() or not consumer.is_file():
        raise RuntimeError("FFI smoke test did not leave both the native artifact and C consumer")
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps({
        "schema_version": 1,
        "claim_id": os.environ.get("LAZILY_CORE_CLAIM_ID"),
        "status": "passed",
        "artifact": str(library.relative_to(root)),
        "consumer": str(consumer.relative_to(root)),
        "invoked": True,
    }, indent=2, sort_keys=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"Core C-ABI proof FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
