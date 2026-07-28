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

test -f "${library}"

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
"${symbol_table[@]}" "${library}" | grep -Eq "[[:space:]_]${symbol}(@@.*)?$"
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
