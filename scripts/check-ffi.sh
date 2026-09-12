#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${repo_root}/build/native-aot"

case "$(uname -m)" in
  x86_64)
    runtime_arch="x64"
    ;;
  arm64 | aarch64)
    runtime_arch="arm64"
    ;;
  *)
    echo "native FFI smoke test is not configured for $(uname -m)" >&2
    exit 1
    ;;
esac

case "$(uname -s)" in
Linux)
runtime_id="linux-${runtime_arch}"
library="${output_dir}/lazily_ffi.so"
symbol_table=(nm -D --defined-only)
;;
Darwin)
runtime_id="osx-${runtime_arch}"
library="${output_dir}/lazily_ffi.dylib"
symbol_table=(nm -gU)
;;
  *)
    echo "native FFI smoke test is not configured for $(uname -s)" >&2
    exit 1
    ;;
esac

runtime_id="${LAZILY_FFI_RID:-${runtime_id}}"

dotnet publish \
  "${repo_root}/src/Lazily.Native/Lazily.Native.csproj" \
  -c Release \
  -r "${runtime_id}" \
  --nologo \
  -o "${output_dir}"

if [ ! -f "${library}" ]; then
  echo "FFI check FAILED: dotnet publish wrote no ${library}." >&2
  echo "      The publish above exited 0, so this is a RID or output-path" >&2
  echo "      mismatch, not a compile failure. A bare \`test -f\` here used to" >&2
  echo "      exit 1 with no message at all." >&2
  exit 1
fi

# Read the symbol table ONCE, then match with a here-string (#lzgrepcpipefail).
#
# This was `"${symbol_table[@]}" "${library}" | grep -Eq ...` inside the loop,
# which has two faults, and neither is a `|| true` case — a missing export is a
# real failure and must stay one.
#
#   * `nm | grep -q` under `set -o pipefail` inverts on a MATCH. `grep -q` exits
#     at the first hit, the producer takes SIGPIPE writing the rest, and pipefail
#     surfaces the producer's death as the pipeline's status. Measured on a
#     2.3MB table with the wanted symbol on line 1: pipeline status 141 while
#     `grep` itself returned 0, and under `set -e` the script died at that line
#     printing nothing. Today's table is 10 lines / 505 bytes, so `nm` finishes
#     inside the pipe buffer and the inversion does not fire — it is latent, and
#     it arms itself silently the moment this library exports enough symbols to
#     exceed one pipe buffer. The same inversion is documented in ci.yml and in
#     check-conformance-coverage.sh, both of which chose here-strings for it.
#   * `nm` failing and a symbol being ABSENT were the same exit code, and both
#     printed nothing. Separating them is why the read is its own statement.
if ! symbols="$("${symbol_table[@]}" "${library}")"; then
  echo "FFI check FAILED: ${symbol_table[*]} could not read ${library}." >&2
  echo "      That is an unreadable artefact, not a missing export." >&2
  exit 1
fi

for symbol in \
  lazily_ffi_ipc_message_validate_json \
  lazily_ffi_ipc_message_kind_json \
  lazily_ffi_ipc_message_clone_json \
  lazily_ffi_bytes_free \
  lazily_ffi_channel_new \
  lazily_ffi_channel_free \
  lazily_ffi_channel_send_json \
  lazily_ffi_channel_recv_json
do
  if ! grep -Eq "[[:space:]_]${symbol}(@@.*)?$" <<< "$symbols"; then
    echo "FFI check FAILED: ${library} exports no '${symbol}'." >&2
    echo "      The native surface lost an entry point; a caller linking against" >&2
    echo "      this .so would fail at load, not at build." >&2
    exit 1
  fi
done

cc \
  -std=c11 \
  -Wall \
  -Wextra \
  -Werror \
  -I"${repo_root}/include" \
  "${repo_root}/tests/native/ffi_smoke.c" \
  "${library}" \
  -Wl,-rpath,"${output_dir}" \
  -o "${output_dir}/ffi-smoke"

"${output_dir}/ffi-smoke"
