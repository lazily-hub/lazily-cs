#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

mkdir -p "$tmp/consumer"
cp "$root/tests/core-artifact-consumer/LazilyConsumer.csproj" "$tmp/consumer/"
cp "$root/tests/core-artifact-consumer/Program.cs" "$tmp/consumer/"
framework=${TARGET_FRAMEWORK:-net10.0}
if [ -n "${LAZILY_CORE_ARTIFACT_VERSION:-}" ]; then
  sed -i "s|Version=\"0.9.0\"|Version=\"$LAZILY_CORE_ARTIFACT_VERSION\"|" "$tmp/consumer/LazilyConsumer.csproj"
  package_source="https://api.nuget.org/v3/index.json"
else
  dotnet pack "$root/src/Lazily/Lazily.csproj" -c Release --nologo -o "$tmp/packages"
  package_source="$tmp/packages"
fi
NUGET_PACKAGES="$tmp/nuget" dotnet restore "$tmp/consumer/LazilyConsumer.csproj" \
  --source "$package_source" \
  --source "https://api.nuget.org/v3/index.json" \
  -p:TargetFramework="$framework"
DOTNET_ROLL_FORWARD=Major NUGET_PACKAGES="$tmp/nuget" dotnet run --project "$tmp/consumer/LazilyConsumer.csproj" --no-launch-profile \
  --no-restore --framework "$framework" \
  -p:TargetFramework="$framework"

if [ -n "${LAZILY_CORE_ARTIFACT_VERSION:-}" ]; then
  grep -Fq 'https://api.nuget.org/v3/index.json' "$tmp/nuget/lazily/$LAZILY_CORE_ARTIFACT_VERSION/.nupkg.metadata"
else
  grep -Fq "$tmp/packages" "$tmp/nuget/lazily/0.9.0/.nupkg.metadata" || {
    echo "consumer did not resolve Lazily from the built package directory" >&2
    exit 1
  }
fi

if grep -R -F "$root" "$tmp/consumer/obj" >/dev/null 2>&1; then
  echo "consumer metadata references the source checkout" >&2
  exit 1
fi
