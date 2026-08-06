#!/usr/bin/env bash
set -euo pipefail

package_dir="$(mktemp -d)"
trap 'rm -rf "$package_dir"' EXIT

expected_version="${EXPECTED_VERSION:-$(dotnet msbuild src/Lazily.R3/Lazily.R3.csproj -nologo -getProperty:Version)}"
package_path="${R3_PACKAGE_PATH:-}"
if [ -z "$package_path" ]; then
    dotnet pack src/Lazily.R3/Lazily.R3.csproj \
        --configuration Release \
        --nologo \
        --output "$package_dir" >/dev/null
    packages=("$package_dir"/Lazily.R3.*.nupkg)
    if [ "${#packages[@]}" -ne 1 ] || [ ! -f "${packages[0]}" ]; then
        echo "R3 package check FAILED: expected exactly one Lazily.R3 nupkg" >&2
        exit 1
    fi
    package_path="${packages[0]}"
fi

if [ ! -f "$package_path" ]; then
    echo "R3 package check FAILED: package not found at '$package_path'" >&2
    exit 1
fi

expected_name="Lazily.R3.${expected_version}.nupkg"
if [ "$(basename "$package_path")" != "$expected_name" ]; then
    echo "R3 package check FAILED: expected '$expected_name', got '$(basename "$package_path")'" >&2
    exit 1
fi

nuspec="$(unzip -p "$package_path" Lazily.R3.nuspec)"
if ! grep -qxF "    <version>${expected_version}</version>" <<< "$nuspec"; then
    echo "R3 package check FAILED: Lazily.R3.nuspec version is not '$expected_version'" >&2
    exit 1
fi
if ! grep -q '<dependency id="Lazily" version="' <<< "$nuspec"; then
    echo "R3 package check FAILED: missing Lazily dependency" >&2
    exit 1
fi
if ! grep -q '<dependency id="R3" version="' <<< "$nuspec"; then
    echo "R3 package check FAILED: missing R3 dependency" >&2
    exit 1
fi

entries="$(unzip -Z1 "$package_path")"
for entry in lib/net10.0/Lazily.R3.dll README.md; do
    if ! grep -qxF "$entry" <<< "$entries"; then
        echo "R3 package check FAILED: missing $entry" >&2
        exit 1
    fi
done

echo "R3 package check OK: Lazily.R3 $expected_version; net10.0 assembly, dependencies, and README"
