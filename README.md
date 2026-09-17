# Jelto for .NET

Dependency-free desktop analytics for .NET 8+ on Windows, macOS and Linux.

The package is published on NuGet as `Jelto`. Add it to your desktop project:

```sh
dotnet add package Jelto
```

To build a local package instead, install .NET SDK 8, Python 3.11 or later and
Make, run `make package` from this component's source root, and add
`--source /absolute/path/to/artifacts` to the command above.

```csharp
using Jelto;

JeltoClient.Initialize("prd_acmedemo01");
JeltoClient.Track("project_created");
JeltoClient.SetProps(new Dictionary<string, string> { ["license"] = "paid" });
JeltoClient.Onboarding("permissions", "ok");
```

Register custom events and their property keys in Settings > Events. Initialize only after
telemetry may start. The SDK creates a random install ID, sends a daily heartbeat and queues the
install claim immediately on first initialization. One product/app per process. Optional named `app` and `endpoint`
arguments select a registered slug and a custom ingest URL; otherwise the endpoint is
`JELTO_ENDPOINT` or `https://in.jelto.io/v1/e`.

For an app with existing users, supply an optional host classification before
changing your saved first-launch state:

```csharp
JeltoClient.Initialize("prd_acmedemo01", installOrigin: InstallOrigin.Existing);
```

Use `InstallOrigin.New` only when the host knows this is the app installation's
first launch, `Existing` when it predates Jelto, or `Unknown` (the default) when
unsure. A missing onboarding-complete flag alone does not prove a new installation.
Only the category is sent on the install claim, never a date or onboarding history.
It remains fixed across retries and relaunches; older claims without it stay
unknown. `Reset()` creates an unknown claim. `Disable()` followed by initialization
can capture a newly supplied category. Do not put `install_origin` in `SetProps`;
heartbeats never carry it.

At initialization the SDK compares the entry assembly's displayed version with its persisted
last known version. A change, including a downgrade, queues `app_updated` with `from_version`
and `to_version`, retaining the install ID and leaving install counts unchanged. First launch
and migration from older SDK state establish a baseline. Missing, blank or overlong versions
do not change it. Updates are detected even after that day's heartbeat has already been sent;
offline retries retain the transition's original ID, time and app metadata. This built-in event
requires no registration and is available through `goal:app_updated` (installs that updated)
and `goal_completions:app_updated` (transitions), with version property breakdowns.

`JeltoClient.InstallId` exposes the ID for privacy requests. `Reset()` rotates it and clears old
events while retaining install properties. `Disable()` cancels delivery and wipes local state;
call `Initialize` to opt in again. Nothing is collected before initialization. Do not send names,
email addresses, IP addresses or a shared web/app identifier in event properties.

Storage uses the current user's local application-data directory, partitioned by entry assembly,
product and app slug. `JELTO_STATE_DIR` replaces the whole directory. Give each live process its
own directory; another writer leaves analytics inactive. Disk failures are contained. The queue
is bounded to 1,000 events/1 MiB and drops oldest events first. A process-exit flush waits at most
600 ms; abrupt exits rely on journal recovery at the next launch. Framework background adapters
are outside this release. Supported process architectures are x64, arm64 and x86 where .NET
supports them. Mobile, Unity, .NET Framework and server API clients are outside v1.

`JeltoClient.Debug = true` or `JELTO_DEBUG=1` prints payloads and diagnostics to stderr.

See the [integration guide](https://jelto.io/docs/sdk/dotnet) for WPF, WinForms and
Avalonia examples; a pinned copy is in `vendor/jelto/dotnet.md` in the source repository.
Local packaging does not publish. The tag release workflow publishes to NuGet.org
after the bootstrap and trusted-publishing setup in RELEASING.md.

## Development

- `make test`: executable, dependency-free regression suite and C11 budgets.
- `make conformance`: shared wire and lifecycle harness (run twice before certification).
- `make package`: NuGet package, reproducible-build comparison and CHECKSUMS.

The test/conformance assemblies access internal inspection seams through InternalsVisibleTo;
those seams are not public configuration APIs. SDK version: 0.2.2. License: MIT.

Run these commands from the SDK directory. Shared conformance requires
`JELTO_CONTRACTS_DIR` pointing to an extracted Jelto contracts **0.1.5** archive.
`make verify-examples` compiles the pinned guide in `vendor/jelto/dotnet.md`
against the local package. Its manifest records the canonical guide and hash;
refresh it from the source repository instead of editing the snapshot.

## Repository CI and releases

The component-owned workflows become active when this directory is the
repository root. CI runs local package tests; release CI additionally requires
conformance twice and the configured contracts pin where applicable.
See [RELEASING.md](https://github.com/usejelto/dotnet-sdk/blob/main/RELEASING.md) for initial publication, trusted publishing,
version tags, and retries. Publishing stays disabled until explicitly configured.

## Community and license

Questions, bug reports and documentation improvements are welcome. See
[Support](https://github.com/usejelto/dotnet-sdk/blob/main/SUPPORT.md),
[Contributing](https://github.com/usejelto/dotnet-sdk/blob/main/CONTRIBUTING.md),
[Code of Conduct](https://github.com/usejelto/dotnet-sdk/blob/main/CODE_OF_CONDUCT.md), and
[Security policy](https://github.com/usejelto/dotnet-sdk/blob/main/SECURITY.md).
Contact [taha@jelto.io](mailto:taha@jelto.io) for anything else.

Jelto-owned software and associated documentation use the [MIT license](LICENSE).
Third-party materials retain their own terms, including the Contributor Covenant
attribution. Jelto names, logos, mascots and original brand artwork are excluded
from the software license; no trademark rights are granted.

## Specification references

Source comments cite `spec/wire-v1.md` (the wire contract: envelope, fields, statuses,
retry rules) and `spec/sdk-conformance.md` (the behavioural contract, whose `C…` and `W…`
identifiers name conformance scenarios). Neither file ships in this repository: both live in
the public contracts repository at <https://github.com/usejelto/contracts/tree/main/spec>.
A comment that states a rule in words and then cites a section is pointing at the normative
text for that rule.
