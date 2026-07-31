# lazily-cs — build, test, and verification targets.

.PHONY: all restore build test format format-check conformance pack package-check ffi-check interop-peer-check check clean conformance-coverage ci-reach

DOTNET ?= dotnet

all: check

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

package-check:
	./scripts/check-package.sh

ffi-check:
	./scripts/check-ffi.sh

interop-peer-check:
	$(DOTNET) run --project src/Lazily.InteropPeer/Lazily.InteropPeer.csproj --no-build -- --self-check

# Full local gate — run before committing.
check: format-check build test conformance-coverage package-check ffi-check interop-peer-check ci-reach
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
	./scripts/check-conformance-coverage.sh
