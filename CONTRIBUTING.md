# Contributing to Jelto for .NET

Bug reports, documentation corrections, examples and focused code changes are
welcome. Participants follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Start a contribution

Search [existing issues](https://github.com/usejelto/dotnet-sdk/issues) before opening one. Use a bug report for
reproducible failures, a feature request to explain a use case, or a blank issue
for questions and documentation feedback. Discuss substantial features and
breaking changes before implementing them. Small fixes can go straight to a PR.

Send security reports privately as described in [SECURITY.md](SECURITY.md).
For installation and account help, see [SUPPORT.md](SUPPORT.md).

## Local development

.NET SDK 8, Make, Python 3.11 or later for release tooling, and the Go toolchain required by the contracts archive. Set `DOTNET=/absolute/path/to/dotnet` when the executable is not on PATH.

Fork and clone this repository, create a branch from `main`, and run commands
from this component's root. The standalone checkout contains its build inputs;
you do not need the private Jelto backend.

Shared conformance needs a verified Jelto contracts **0.1.5** archive. Download
it and its checksum from the [contracts releases](https://github.com/usejelto/contracts/releases)
when published, then install it into an empty directory:

```sh
python3 vendor/test-tools/install.py /absolute/path/jelto-contracts-0.1.5.zip SHA256 /absolute/path/contracts --version 0.1.5
export JELTO_CONTRACTS_DIR=/absolute/path/contracts
```

Replace `SHA256` with the checksum from that release. Use the Go version in the
archive's `go.mod`. Before the first public release, obtain the reviewed archive
from the maintainer. Monorepo contributors may explicitly point
`JELTO_CONTRACTS_DIR` at the local contracts root. Builds must not discover a
sibling checkout implicitly.

```sh
make test
make conformance-twice
make package
make verify-examples
```

Run the smallest relevant checks while iterating, then the affected gates above.
Documentation-only changes need link and example review; SDK behavior or wire
changes require the appropriate tests and two consecutive conformance passes
before certification. Package changes must also pass packaging checks.

Keep regressions in the executable `Jelto.Tests/` suite. `make test` includes
C11 budgets; `make package` checks C19 reproducibility. `make verify-examples`
compiles the pinned guide snippets and exercises a consumer of the local package.
For WPF, WinForms and Avalonia framework builds, also run
`python3 verify-examples.py --desktop` after packaging (set `DOTNET` in the
environment if needed). Do not edit `vendor/jelto/` or `vendor/test-tools/` directly; propose a
change to the authoritative input and update its version and checksums. Use the
contracts wire and conformance specifications for expected behavior.

## Review and acceptance

Keep changes focused and match the surrounding style.
Follow the existing C# naming and nullable annotations. Preserve the dependency-free runtime package.
Add regression coverage for behavior changes. Do not update expected values just to match a
failing implementation, and do not edit generated or vendored inputs by hand.

Use a scoped Conventional Commit title, such as `fix(dotnet): clarify retry handling` or `docs(dotnet): explain local installation`. Describe the problem,
the resulting behavior, relevant issue/specification, compatibility impact and
exact verification commands with results. Include screenshots for visible UI
changes. State when a check was not run and why. A separate specification change
should precede its implementation; fixture expectation changes need a separate
commit explaining their derivation.

Use synthetic data in examples and reports. Never include credentials, customer
payloads or IP addresses in public logs. Contributions must preserve Jelto's
privacy boundaries: no IP storage or logging, and no row-level identifier that
joins a website visitor to an app install.

Taha Bozdemir reviews scope, correctness, tests and compatibility, and decides
whether to merge. Review is best effort with no guaranteed turnaround. Maintainers
may request changes or decline work outside the component's purpose; explain
tradeoffs in the issue or PR so future contributors can follow the decision.

## Licensing and releases

Submit only work you have the right to contribute, under this repository's
[MIT license](LICENSE). Preserve third-party notices and the Contributor Covenant
attribution. Jelto names, logos, mascots and original brand artwork are excluded
from the software license; no trademark rights are granted. No separate CLA or
DCO sign-off is required.

Maintainers publish releases using [RELEASING.md](RELEASING.md). Local packaging
does not publish, and a successful test run is not a release announcement.
