namespace Jelto;

/// <summary>Host knowledge of the app installation before its first Jelto initialization.</summary>
public enum InstallOrigin
{
    /// <summary>The host does not know whether the app installation is new.</summary>
    Unknown,
    /// <summary>The host knows this is the app installation's first launch.</summary>
    New,
    /// <summary>The app installation existed before Jelto initialization.</summary>
    Existing
}

/// <summary>Explicit, privacy-focused analytics for one desktop product/app per process.</summary>
public static class JeltoClient
{
    private static readonly Engine engine = new();
    /// <summary>Starts analytics asynchronously. Call once after deciding telemetry may start.</summary>
    /// <param name="key">Your product key (prd_ followed by ten lowercase letters or digits).</param>
    /// <param name="app">Optional registered app slug; otherwise the server uses the desktop OS.</param>
    /// <param name="endpoint">Optional absolute HTTP(S) ingest URL. Overrides JELTO_ENDPOINT and the production default.</param>
    /// <param name="installOrigin">Optional host classification, captured once for the install claim. Defaults to unknown.</param>
    public static void Initialize(string key, string? app = null, string? endpoint = null, InstallOrigin installOrigin = InstallOrigin.Unknown) => engine.Initialize(key, app, endpoint, installOrigin);
    /// <summary>Queues an event. Properties accept strings, finite numbers and booleans; invalid events are dropped.</summary>
    public static void Track(string name, IReadOnlyDictionary<string, object?>? props = null) => engine.Track(name, props);
    /// <summary>Merges valid install properties and sends a heartbeat on change. Values persist across launches.</summary>
    public static void SetProps(IReadOnlyDictionary<string, string> props) => engine.SetProps(props);
    /// <summary>Tracks onboarding:&lt;step&gt; with status ok, fail or skip and an optional short reason code.</summary>
    public static void Onboarding(string step, string status, string? reason = null) => engine.Onboarding(step, status, reason);
    /// <summary>The persistent UUIDv4 install ID; empty before initialization or after disabling.</summary>
    public static string InstallId => engine.InstallId;
    /// <summary>Rotates the identity, clears queued events and starts a new install while retaining install properties.</summary>
    public static void Reset() => engine.Reset();
    /// <summary>Cancels delivery and deletes local identity, properties and events. Initialize can re-enable analytics.</summary>
    public static void Disable() => engine.Disable();
    /// <summary>When enabled, writes diagnostics and exact outgoing payloads to stderr. Defaults to JELTO_DEBUG=1.</summary>
    public static bool Debug { get => engine.Debug; set => engine.Debug = value; }
    internal static Engine Conformance => engine;
}
