# Contributing to lazily-cs

## Set up the conformance corpus

The shared conformance corpus is not vendored. Clone `lazily-spec` beside this
repository before running the suite:

```bash
git clone https://github.com/lazily-hub/lazily-spec.git ../lazily-spec
```

## Verify a change

```bash
make check          # build + test — run before committing
make conformance    # replay the shared lazily-spec fixtures only
make format-check   # dotnet format --verify-no-changes
```
