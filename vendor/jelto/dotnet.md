---
title: ".NET SDK"
group: sdk
slug: sdk/dotnet
summary: "Initialize Jelto in a .NET desktop app and track a registered action."
---

# .NET SDK

Measure desktop app activity and registered actions from your .NET application.

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

## Verify

Launch the app after enabling telemetry in your own app. Register `export_finished` and its `format` property in **Settings → Events & funnels → Events** before sending the example. Trigger an export, then inspect the product's app activity and Goals for the current date. Allow up to one minute after changing event registration.

The SDK sends daily activity and queues the first install claim with a delay of up to six hours. A new integration can therefore send activity before an install appears. Release versions support version-adoption reporting; later version changes are reported without creating a new install. Retention requires elapsed time and a mature sample.

If data is missing, check the product ID, registered app slug, collection permission and network access. Debug payload logging is for local diagnosis only; turn it off before distributing a build. Do not reset the install ID on each launch. See [Understand app usage](../guides/understand-app-usage.md).

## Lifecycle

`JeltoClient.SetProps` updates install properties. `JeltoClient.Disable()` stops delivery and wipes local analytics state. `JeltoClient.Reset()` rotates the install ID, which is available as `JeltoClient.InstallId` for app-data requests. Reinitialize only after the app permits analytics again.

Call `JeltoClient.Debug = true` only during local diagnosis. Each live process must use its own state directory. Test the distributed app as well as your IDE build.
