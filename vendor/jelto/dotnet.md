---
title: ".NET SDK"
group: sdk
slug: sdk/dotnet
summary: "Initialize Jelto in a .NET desktop app and track a custom action."
---

# .NET SDK

Measure desktop app activity and custom actions from your .NET application.

For an established app, read [Add Jelto to an app with existing users](../start/existing-app.md)
before interpreting install counts, update history or cohorts.

## Set up with AI

For a copyable setup prompt with your app details, open **Settings → Installation
→ Apps**, expand your app's **SDK setup**, choose **.NET** and select **Copy prompt**.
The prompt names the package described below. See
[Set up with AI](../start/apps.md#set-up-with-ai) for what to expect.

## Setup steps

1. Add the `Jelto` NuGet package to your desktop project.
2. Initialize once during application startup, using the registered app slug.
3. Track an action only after it succeeds.
4. Launch the app and verify activity and the event in Jelto.

## Install

Requires .NET 8 or later. This desktop SDK supports Windows, macOS and Linux. The package is published on NuGet as `Jelto`; its source and releases are at [usejelto/dotnet-sdk](https://github.com/usejelto/dotnet-sdk). Add the current release to your desktop project:

```sh
dotnet add package Jelto
```

The API uses the `Jelto` namespace. Mobile, Unity and .NET Framework are outside this desktop integration.

## Initialize once

In WPF, initialize in your application startup flow; in WinForms, before the main form begins work; in Avalonia, after application initialization. In each case, first apply your app's saved analytics choice.

```csharp
using Jelto;

JeltoClient.Initialize("YOUR_PRODUCT_ID", app: "desktop");
```

Use the registered app slug from **Settings → Installation → Apps**. In your successful export handler:

```csharp
JeltoClient.Track("export_finished",
    new Dictionary<string, object?> { ["format"] = "pdf" });
```

## New and existing installations

When using a version with install-origin support, pass the host's classification
at initialization. For an installation that already existed before Jelto:

```csharp
JeltoClient.Initialize("YOUR_PRODUCT_ID", app: "desktop", installOrigin: InstallOrigin.Existing);
```

Use `InstallOrigin.New` only when the host knows this is the app's first launch;
use `InstallOrigin.Existing` for a saved earlier installation and
`InstallOrigin.Unknown` when uncertain (the default).
Read saved host state before changing it; never send a first-launch date.
The first claim freezes the classification across retries and later launches.
Older claims stay unknown and are excluded from new-install cohorts. See
[Add Jelto to an app with existing users](../start/existing-app.md) for rollout,
coverage and cohort rules.

## Onboarding and custom events

The onboarding call sends `onboarding:<step>` with a status and optional reason:

```csharp
JeltoClient.Onboarding("permissions", "ok");
JeltoClient.Onboarding("permissions", status: "fail", reason: "denied");
```

The step must match `^[a-z0-9_-]{1,32}$`: 1–32 lowercase ASCII letters, digits, underscores or hyphens. Status must be `ok`, `fail` or `skip`. A non-empty reason must match `^[a-z0-9_.-]+$` and be at most 64 characters; an omitted or empty reason sends no reason property. If any of these values falls outside the grammar or length cap, the whole event is dropped. The rejection is visible only in debug logging, not in the dashboard.

Enable local debug logging with `JeltoClient.Debug = true`.

Steps feed `onboarding_reached`, `onboarding_ok`, `onboarding_fail` and `onboarding_skip`, broken down by `onboarding_step`; status reports use each install's first result for that step. They also feed `onboarding_reason`, which groups failures by `onboarding_reason` and requires an `onboarding_step` filter. `onboarding_cohort` supplies the explicitly new install population (existing and unknown origins are excluded), and `onboarding_completed` uses the final step's `ok` result.

Every valid `JeltoClient.Track()` event received by Jelto becomes `goal:<name>` on the app surface, with per-install conversion and `prop:<key>` breakdowns. Custom events and their property keys are discovered on first receipt; no event or goal registration is required. For the export example, query `goal:export_finished` with `surface=app` and `dimension=prop:format`.

The source of truth for these SDK event contracts is [spec/wire-v1.md §4](https://github.com/usejelto/contracts/blob/main/spec/wire-v1.md#4-reserved-event-names); §7 of the same document defines custom event discovery.

## License properties

Update the install's license when its state changes:

```csharp
JeltoClient.SetProps(new Dictionary<string, string> { ["license"] = "paid" });
```

Accepted license values are strings matching `^[a-z0-9_.-]{1,24}$`, such as `free`, `trial`, `paid` or `expired`; these examples are not a fixed enum. Uppercase letters, spaces and values longer than 24 characters are rejected. `license_share` breaks the live install fleet down by the stored `license` value. `license_conversion` counts an install as converted only when its latest stored `license` equals the product's `paid_license_value` setting by string equality; the setting defaults to `paid`. If no stored values ever match, an otherwise reportable cohort stays at a true, permanent 0 % even though the query succeeds; align the value in the product settings or through the account API's `paid_license_value` field.

## Verify

Launch the app after enabling telemetry in your own app. Trigger an export, then inspect the product's app activity and Goals for the current date. Jelto discovers `export_finished` and its `format` property when it receives the event; no event registration is required.

The SDK sends daily activity and queues the first install claim immediately on first initialization. Release versions support version-adoption reporting; later version changes are reported without creating a new install. Retention requires elapsed time and a mature sample.

If data is missing, check the product ID, registered app slug, collection permission and network access. Debug payload logging is for local diagnosis only; turn it off before distributing a build. Do not reset the install ID on each launch. See [Understand app usage](../guides/understand-app-usage.md).

## Lifecycle

`JeltoClient.SetProps` updates install properties. `JeltoClient.Disable()` stops delivery and wipes local analytics state. `JeltoClient.Reset()` rotates the install ID, which is available as `JeltoClient.InstallId` for app-data requests. Reinitialize only after the app permits analytics again.

Call `JeltoClient.Debug = true` only during local diagnosis. Each live process must use its own state directory. Test the distributed app as well as your IDE build.

## Track updater outcomes

Use the existing tracking API to send `app_update` stages, failures, and explicit postponements. They appear under **App updates → Update activity**, separately from version changes confirmed on launch. See [Track update activity](../guides/understand-app-usage.md#track-update-activity) for properties and updater callbacks.
