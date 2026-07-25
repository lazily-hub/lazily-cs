# lazily-cs — build, test, and verification targets.

.PHONY: all restore build test format format-check conformance pack check clean

DOTNET ?= dotnet

all: check

restore:
	$(DOTNET) restore

build:
	$(DOTNET) build --nologo

test:
	$(DOTNET) test --nologo

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

# Full local gate — run before committing.
check: build test
	@echo "lazily-cs: check OK"

clean:
	$(DOTNET) clean --nologo
