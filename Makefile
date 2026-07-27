# lazily-cs — build, test, and verification targets.

.PHONY: all restore build test format format-check conformance pack package-check check clean conformance-coverage

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

format:
	$(DOTNET) format

# Formatting is verified, not applied, in CI.
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

# Full local gate — run before committing.
check: build test conformance-coverage package-check
	@echo "lazily-cs: check OK"

clean:
	$(DOTNET) clean --nologo

# Conformance-coverage guard (#portconformancecoverage). Static: fails when the
# canonical corpus grows a fixture no test in this repo even names. Naming is not
# replaying — see the script header for what this does and does not prove.
conformance-coverage:
	./scripts/check-conformance-coverage.sh
