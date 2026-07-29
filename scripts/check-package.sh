#!/usr/bin/env bash
set -euo pipefail

package_dir="$(mktemp -d)"
trap 'rm -rf "$package_dir"' EXIT

expected_version="${EXPECTED_VERSION:-$(dotnet msbuild src/Lazily/Lazily.csproj -nologo -getProperty:Version)}"
package_path="${PACKAGE_PATH:-}"
if [ -z "$package_path" ]; then
    dotnet pack src/Lazily/Lazily.csproj \
        --configuration Release \
        --nologo \
        --output "$package_dir" >/dev/null
    packages=("$package_dir"/Lazily.*.nupkg)
    if [ "${#packages[@]}" -ne 1 ] || [ ! -f "${packages[0]}" ]; then
        echo "package check FAILED: expected exactly one Lazily nupkg" >&2
        exit 1
    fi
    package_path="${packages[0]}"
fi

if [ ! -f "$package_path" ]; then
    echo "package check FAILED: package not found at '$package_path'" >&2
    exit 1
fi

expected_name="Lazily.${expected_version}.nupkg"
if [ "$(basename "$package_path")" != "$expected_name" ]; then
    echo "package check FAILED: expected '$expected_name', got '$(basename "$package_path")'" >&2
    exit 1
fi

nuspec="$(unzip -p "$package_path" Lazily.nuspec)"
if ! grep -qxF "    <version>${expected_version}</version>" <<< "$nuspec"; then
    echo "package check FAILED: Lazily.nuspec version is not '$expected_version'" >&2
    exit 1
fi

entries="$(unzip -Z1 "$package_path")"
assembly_version="${expected_version%%[-+]*}.0"
for target in netstandard2.1 net8.0 net10.0; do
    if ! grep -qxF "lib/$target/Lazily.dll" <<< "$entries"; then
        echo "package check FAILED: missing lib/$target/Lazily.dll" >&2
        exit 1
    fi
    assembly_path="$package_dir/Lazily-$target.dll"
    unzip -p "$package_path" "lib/$target/Lazily.dll" > "$assembly_path"
    dotnet msbuild scripts/check-assembly-version.proj \
        -nologo \
        -verbosity:quiet \
        -target:Check \
        -property:AssemblyPath="$assembly_path" \
        -property:ExpectedVersion="$assembly_version"
done

if ! grep -qxF "README.md" <<< "$entries"; then
    echo "package check FAILED: missing README.md" >&2
    exit 1
fi

echo "package check OK: Lazily $expected_version; netstandard2.1 + net8.0 + net10.0 assemblies and README"
