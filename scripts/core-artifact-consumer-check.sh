#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

dotnet pack "$root/src/Lazily/Lazily.csproj" -c Release --nologo -o "$tmp/packages"
mkdir -p "$tmp/consumer"
cp "$root/tests/core-artifact-consumer/LazilyConsumer.csproj" "$tmp/consumer/"
cp "$root/tests/core-artifact-consumer/Program.cs" "$tmp/consumer/"
framework=${TARGET_FRAMEWORK:-net10.0}
NUGET_PACKAGES="$tmp/nuget" dotnet restore "$tmp/consumer/LazilyConsumer.csproj" \
  --source "$tmp/packages" \
  --source "https://api.nuget.org/v3/index.json" \
  -p:TargetFramework="$framework"
DOTNET_ROLL_FORWARD=Major NUGET_PACKAGES="$tmp/nuget" dotnet run --project "$tmp/consumer/LazilyConsumer.csproj" --no-launch-profile \
  --no-restore --framework "$framework" \
  -p:TargetFramework="$framework"

grep -Fq "$tmp/packages" "$tmp/nuget/lazily/0.8.0/.nupkg.metadata" || {
  echo "consumer did not resolve Lazily from the built package directory" >&2
  exit 1
}

if grep -R -F "$root" "$tmp/consumer/obj" >/dev/null 2>&1; then
  echo "consumer metadata references the source checkout" >&2
  exit 1
fi
