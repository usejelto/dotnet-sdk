.DEFAULT_GOAL := test
.PHONY: build test conformance conformance-run conformance-twice package verify-examples
DOTNET ?= dotnet
JELTO_CONTRACTS_DIR ?=
JELTO_CONTRACTS_VERSION = $(shell python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$(JELTO_CONTRACTS_DIR)/spec/contracts/manifest.json")
CONFORMANCE_HOST = $(if $(filter Windows_NT,$(OS)),$(CURDIR)/ConformanceHost/bin/Release/net8.0/ConformanceHost.exe,$(CURDIR)/conformance-host)

build:
	DOTNET_CLI_TELEMETRY_OPTOUT=1 $(DOTNET) build ConformanceHost -c Release -p:UseSharedCompilation=false

test:
	DOTNET_CLI_TELEMETRY_OPTOUT=1 $(DOTNET) run --project Jelto.Tests -c Release -p:UseSharedCompilation=false

conformance: build
	$(MAKE) conformance-run

# The runner invocation alone, for a tree `build` has already produced.
conformance-run:
	@test -n "$(JELTO_CONTRACTS_DIR)" || { echo 'Set JELTO_CONTRACTS_DIR to a verified Jelto contracts archive.' >&2; exit 1; }
	DOTNET="$(DOTNET)" go -C "$(JELTO_CONTRACTS_DIR)" run ./spec/conformance/runner -contracts-version "$(JELTO_CONTRACTS_VERSION)" -host "$(CONFORMANCE_HOST)"

# spec/sdk-conformance.md §1: every scenario passes on a clean machine, twice.
# The passes share nothing -- each runner starts its own mockd on port 0 with a
# private control socket and gives every scenario a fresh JELTO_STATE_DIR under
# its own temporary directory -- so they run concurrently and the gate takes one
# pass's wall time, not two. The first pass's output is replayed once the second
# has finished so the log reads as two complete passes.
conformance-twice: build
	$(MAKE) conformance-run > "$${TMPDIR:-/tmp}/jelto-conformance-$$$$.log" 2>&1 & pid=$$!; \
	$(MAKE) conformance-run; second=$$?; \
	wait $$pid; first=$$?; \
	echo; echo '--- first pass (ran concurrently with the one above) ---'; \
	cat "$${TMPDIR:-/tmp}/jelto-conformance-$$$$.log"; rm -f "$${TMPDIR:-/tmp}/jelto-conformance-$$$$.log"; \
	test "$$first" -eq 0 && test "$$second" -eq 0

package:
	DOTNET="$(DOTNET)" python3 pack.py

verify-examples: package
	DOTNET="$(DOTNET)" python3 verify-examples.py
