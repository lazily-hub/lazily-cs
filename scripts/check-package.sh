#!/usr/bin/env bash
set -euo pipefail

package_dir="$(mktemp -d)"
trap 'rm -rf "$package_dir"' EXIT

dotnet pack src/Lazily/Lazily.csproj \
    --configuration Release \
    --nologo \
    --output "$package_dir" >/dev/null

packages=("$package_dir"/Lazily.*.nupkg)
if [ "${#packages[@]}" -ne 1 ] || [ ! -f "${packages[0]}" ]; then
    echo "package check FAILED: expected exactly one Lazily nupkg" >&2
    exit 1
fi

entries="$(unzip -Z1 "${packages[0]}")"
for target in netstandard2.1 net8.0 net10.0; do
    if ! grep -qxF "lib/$target/Lazily.dll" <<< "$entries"; then
        echo "package check FAILED: missing lib/$target/Lazily.dll" >&2
        exit 1
    fi
done

if ! grep -qxF "README.md" <<< "$entries"; then
    echo "package check FAILED: missing README.md" >&2
    exit 1
fi

echo "package check OK: netstandard2.1 + net8.0 + net10.0 assemblies and README"
