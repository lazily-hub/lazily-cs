# lazily-cs — build, test, and verification targets.

.PHONY: all restore build test format format-check conformance pack package-check api-compat-check ffi-check interop-peer-check benchmark-r3 check clean conformance-coverage assertion-ordering-check ci-reach core-artifact-consumer-check core-evidence-check

DOTNET ?= dotnet

# ---- One run id per `make check` invocation (#lzstalemanifest) ---------------
#
# Every rung in `scripts/check-conformance-coverage.sh` reads ONE evidence file,
# `build/conformance-fixtures-loaded.txt`, and each of its "these bytes were
# really read" claims is only true of the run that wrote it. Nothing in the file
# said WHICH run that was. The guard is a SEPARATE process from the recorder — the
# recorder lives inside the `dotnet test` host, the guard is a shell script a
# later target runs — so the only thing that made the evidence this run's was the
# `: >` truncation in the `test` recipe below, an invariant re-spelled by hand in
# `.github/workflows/ci.yml` and absent from every other path that runs the guard.
# `make conformance-coverage` alone reported on whatever run last wrote the file,
# green, over source that had since changed.
#
# So the file now carries the id of the run that wrote it and every guard REQUIRES
# it to equal this invocation's. Generated with `uuidgen`, falling back to
# nanosecond epoch where that is missing.
#
# `:=` is load-bearing. A recursively-expanded `=` re-runs `uuidgen` at every
# reference, so `test` would stamp one id and `conformance-coverage` would demand
# a different one — a gate that fails closed, but for a reason that reads like the
# bug it is guarding against.
CONFORMANCE_RUN_ID := $(shell uuidgen 2>/dev/null || date +%s%N)

all: check

core-artifact-consumer-check:
	./scripts/core-artifact-consumer-check.sh

# Per-binding Core evidence gate (#lzbindingevidenceci). Runs lazily-spec's
# verifier for this binding alone: it regenerates the Core report through
# scripts/core-conformance.json's report_command, then checks it against the
# canonical profile, so stale evidence handles, unreported skips and a missing
# report are red on push instead of only at cohort qualification.
# --require-all turns an absent ../lazily-spec layout or declaration into a
# failure, never a skip.
core-evidence-check:
	node ../lazily-spec/scripts/check-core-evidence.mjs --bindings-root .. --binding csharp --reports build/core-evidence --generate-reports --require-all

restore:
	$(DOTNET) restore

build:
	$(DOTNET) build --nologo

# The manifest path must be ABSOLUTE. The recorder runs inside the dotnet test
# host, whose working directory is the test project's output dir, not this one —
# a relative path silently writes the manifest somewhere nothing reads it, and
# the guard then fails with "missing evidence" while the suite is green.
test:
	@mkdir -p build && : > build/conformance-fixtures-loaded.txt
	LAZILY_CONFORMANCE_RUN_ID=$(CONFORMANCE_RUN_ID) \
	LAZILY_CONFORMANCE_MANIFEST=$(CURDIR)/build/conformance-fixtures-loaded.txt $(DOTNET) test --nologo

# The repairing form. Deliberately NOT in `check` (#lzruffautofixvacuity): a
# formatter that rewrites the tree it is judging exits 0 no matter what it
# found, so putting this in a gate makes the gate unfailable.
format:
	$(DOTNET) format

# The GATE. Verified, not applied — and now in `check`, so a developer runs
# locally what CI enforces instead of discovering the difference on push.
format-check:
	$(DOTNET) format --verify-no-changes

# Replay the shared lazily-spec conformance fixtures. They resolve through the
# sibling-relative ../lazily-spec/conformance path and are never vendored here.
conformance:
	$(DOTNET) test --nologo --filter "FullyQualifiedName~Conformance"

pack:
	$(DOTNET) pack src/Lazily/Lazily.csproj -c Release --nologo
	$(DOTNET) pack src/Lazily.R3/Lazily.R3.csproj -c Release --nologo

package-check:
	./scripts/check-package.sh
	./scripts/check-r3-package.sh

api-compat-check:
	$(DOTNET) build src/Lazily/Lazily.csproj -c Release --nologo
	$(DOTNET) build src/Lazily.R3/Lazily.R3.csproj -c Release --nologo
	$(DOTNET) build compat/core-generation-1/Lazily.CoreGeneration1.csproj -c Release --nologo
	$(DOTNET) build tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --nologo
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- check api/core-api-v1.json src/Lazily/bin/Release/net10.0/Lazily.dll
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- self-test api/core-api-v1.json src/Lazily/bin/Release/net10.0/Lazily.dll
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- check api/r3-api-v1.json src/Lazily.R3/bin/Release/net10.0/Lazily.R3.dll
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- self-test api/r3-api-v1.json src/Lazily.R3/bin/Release/net10.0/Lazily.R3.dll
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- check api/r3-core-compatibility-v1.json compat/core-generation-1/bin/Release/net10.0/Lazily.dll
	$(DOTNET) run --project tools/Lazily.ApiCompat/Lazily.ApiCompat.csproj --no-build -- self-test api/r3-core-compatibility-v1.json compat/core-generation-1/bin/Release/net10.0/Lazily.dll
	$(DOTNET) build src/Lazily.R3/Lazily.R3.csproj -c Release --nologo -p:LazilyCoreCompatibilityProject=$(CURDIR)/compat/core-generation-1/Lazily.CoreGeneration1.csproj

ffi-check:
	./scripts/check-ffi.sh

interop-peer-check:
	$(DOTNET) run --project src/Lazily.InteropPeer/Lazily.InteropPeer.csproj --no-build -- --self-check

benchmark-r3:
	$(DOTNET) run -c Release --project benchmarks/Lazily.R3.Benchmarks/Lazily.R3.Benchmarks.csproj

# Full local gate — run before committing.
assertion-ordering-check:
	python3 ../lazily-spec/scripts/check-assertion-ordering.py --binding cs --root .

check: format-check build test conformance-coverage package-check api-compat-check ffi-check interop-peer-check assertion-ordering-check core-artifact-consumer-check core-evidence-check ci-reach
	@echo "lazily-cs: check OK"

# CI-reachability guard (#lzcheckcireachguard). Fails when a target above runs a
# gate no CI workflow step reaches — the drift that hid #lzinteroppeerci in every
# binding for months. It guards itself: `ci-reach` is in `check`, so CI has to run
# it too or this target reports itself missing.
ci-reach:
	./scripts/check-ci-reach.sh

clean:
	$(DOTNET) clean --nologo

# Conformance-coverage guard (#portconformancecoverage). Static: fails when the
# canonical corpus grows a fixture no test in this repo even names. Naming is not
# replaying — see the script header for what this does and does not prove.
conformance-coverage:
	LAZILY_CONFORMANCE_RUN_ID=$(CONFORMANCE_RUN_ID) ./scripts/check-conformance-coverage.sh
