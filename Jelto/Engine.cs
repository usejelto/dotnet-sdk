using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Jelto;

internal sealed class Engine : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Queue<Action> commands = new();
    private readonly EventBuffer events = new();
    private readonly Transport transport = new();
    private readonly string? stateOverride, environmentEndpoint, mock, versionOverride;
    private readonly string? platformOverride;
    private readonly string? appVersionOverride;
    private readonly Action<string>? beforeStorageReplace;
    private Thread? worker;
    private bool initialized, accepting, quitting, dirty, exitRegistered, workerBusy;
    private long generation;
    private string id = "";
    private State state = new();
    private Storage? storage;
    private Uri endpoint = new("https://in.jelto.io/v1/e");
    private string key = "", os = "", arch = "", appVersion = "", osVersion = "";
    private string? slug, version;
    private string? knownAppVersion;
    private EventMetadata? metadata;
    private BigInteger? clockPin, initAt, trackAt;
    private bool pending, installEnqueuedThisRun;
    private Flight? flight;
    private CancellationTokenSource? requestCancellation;
    private bool debug;
    private int peakAccounted;
    internal bool Debug { get => Volatile.Read(ref debug); set => Volatile.Write(ref debug, value); }
    internal bool ClockPinned { get { lock (gate) return clockPin.HasValue; } }
    internal string InstallId { get { Barrier(); lock (gate) return id; } }

    internal Engine(string? stateDir = null, string? endpointOverride = null, string? pin = null, string? platform = null, string? appVersion = null, Action<string>? beforeStorageReplace = null)
    {
        stateOverride = stateDir ?? Environment.GetEnvironmentVariable("JELTO_STATE_DIR");
        environmentEndpoint = endpointOverride ?? Environment.GetEnvironmentVariable("JELTO_ENDPOINT");
        mock = Environment.GetEnvironmentVariable("JELTO_MOCK");
        versionOverride = Environment.GetEnvironmentVariable("JELTO_CLIENT_VERSION");
        platformOverride = platform;
        appVersionOverride = appVersion ?? Environment.GetEnvironmentVariable("JELTO_APP_VERSION");
        this.beforeStorageReplace = beforeStorageReplace;
        Debug = Environment.GetEnvironmentVariable("JELTO_DEBUG") == "1";
        var raw = pin ?? Environment.GetEnvironmentVariable("JELTO_NOW");
        clockPin = Wire.Instant(raw?.Trim());
        if (raw is not null && clockPin is null) Log("invalid JELTO_NOW; using wall clock");
    }
    internal void Log(string message)
    {
        if (!Debug) return;
        try { Console.Error.WriteLine("jelto: " + message); } catch { }
    }
    private void Safe(Action action) { try { action(); } catch { Log("SDK operation failed; host continues"); } }
    private BigInteger Now() { lock (gate) return clockPin ?? new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
    private static string Day(BigInteger now)
    {
        var day = BigInteger.DivRem(now, 86400000, out var rem);
        return Wire.Decimal(rem < 0 ? day - 1 : day);
    }
    internal void Initialize(string product, string? app = null, string? url = null, InstallOrigin installOrigin = InstallOrigin.Unknown) => Safe(() => {
        lock (gate)
        {
            if (initialized || quitting) return;
            metadata = null;
            initialized = accepting = true;
            if (!exitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += OnExit; exitRegistered = true;
            }
            var at = Now();
            commands.Enqueue(() => { try { Bootstrap(product, app, url, at, installOrigin); } catch { Inactive("SDK initialization failed"); } });
            // Started directly rather than via the ThreadPool: a saturated pool must never
            // delay the worker's own dedicated thread, or every Invoke behind it stalls too.
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Jelto analytics" };
                worker.Start();
            }
            wake.Set();
        }
    });
    internal void Track(string name, IReadOnlyDictionary<string, object?>? props = null) => Safe(() => {
        long epoch;
        lock (gate) { if (!accepting) return; epoch = generation; }
        var encoded = Wire.Props(name, props, Log);
        if (encoded is null) return;
        var now = Now();
        lock (gate)
        {
            if (!accepting || epoch != generation) return;
            var e = EventRecord.Create(name, Wire.Decimal(now), encoded, metadata: metadata);
            Add(e); trackAt = now + 5000; wake.Set();
        }
    });
    internal void Onboarding(string step, string status, string? reason) => Safe(() => {
        if (step is null || !Wire.Step().IsMatch(step)) { Log("drop onboarding: `<step>` is ^[a-z0-9_-]{1,32}$"); return; }
        var props = new Dictionary<string, object?> { ["status"] = status };
        if (!string.IsNullOrEmpty(reason)) props["reason"] = reason;
        Track("onboarding:" + step, props);
    });
    internal void SetProps(IReadOnlyDictionary<string, string> props) => Safe(() => {
        long epoch;
        lock (gate) { if (!accepting) return; epoch = generation; }
        var accepted = Wire.InstallProps(props, Log);
        if (accepted.Count == 0) return;
        Post(() => {
            if (!accepting || epoch != generation) return;
            var merged = new Dictionary<string, string>(state.InstallProps, StringComparer.Ordinal);
            foreach (var p in accepted) merged[p.Key] = p.Value;
            if (merged.Count > 20) { Log("drop setprops: spec/wire-v1.md §3 caps them at 20"); return; }
            if (merged.Count == state.InstallProps.Count && merged.All(p => state.InstallProps.TryGetValue(p.Key, out var old) && old == p.Value)) return;
            state.InstallProps = merged; Save();
            AddHeartbeat();
            pending = true;
        });
    });
    private void Add(EventRecord e, bool first = false)
    {
        if (events.Add(e, first) > 0) Log("queue cap reached; dropped oldest events");
        dirty = true;
        Account();
    }
    private void AddHeartbeat(bool first = false) => Add(EventRecord.Create("heartbeat", Wire.Decimal(Now()), "{}"u8.ToArray(), true, metadata), first);
    internal void Reset() => Safe(() => {
        lock (gate) { if (!accepting) return; generation++; requestCancellation?.Cancel(); }
        Invoke(() => {
            if (!accepting) return;
            flight = null; events.Clear();
            // Never put a new identity beside the old identity's durable queue.
            // Calls admitted during this reset remain in memory until the new
            // identity is committed; the checkpoint here is explicitly empty.
            if (storage?.Sync([], true) != true) { Inactive("could not clear queue for reset; SDK inactive"); return; }
            var props = state.InstallProps;
            state = new() { InstallProps = props, LastAppVersion = knownAppVersion }; installEnqueuedThisRun = false;
            CreateIdentity(Now()); state.LastHeartbeatDay = Day(Now()); AddHeartbeat();
            if (!Save()) { Inactive("could not persist reset identity; SDK inactive"); return; }
            pending = true; Persist(true);
        });
    });
    internal void Disable() => Safe(() => {
        lock (gate)
        {
            if (!initialized) return;
            accepting = false; generation++; id = ""; requestCancellation?.Cancel();
        }
        Invoke(() => {
            flight = null; events.Clear(); state = new(); pending = false; initAt = trackAt = null;
            storage?.Wipe(); storage = null; dirty = false;
            lock (gate) { initialized = false; id = ""; }
        });
    });
    private void Bootstrap(string product, string? app, string? url, BigInteger now, InstallOrigin installOrigin)
    {
        os = platformOverride ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? "linux" : "");
        arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86", _ => "" };
        if (os is not ("windows" or "macos" or "linux") || arch == "" || product is null || !Wire.Product().IsMatch(product)
            || (!string.IsNullOrEmpty(app) && !Wire.Slug().IsMatch(app)))
        { Inactive("unsupported desktop platform, product key or app slug"); return; }
        key = product; slug = string.IsNullOrEmpty(app) ? null : app;
        var rawEndpoint = !string.IsNullOrEmpty(url) ? url : !string.IsNullOrEmpty(environmentEndpoint) ? environmentEndpoint : "https://in.jelto.io/v1/e";
        if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var resolved) || resolved.Scheme is not ("http" or "https") || resolved.UserInfo != "")
        { Inactive("invalid endpoint; SDK inactive"); return; }
        endpoint = resolved;
        var assembly = Assembly.GetEntryAssembly();
        knownAppVersion = Wire.KnownAppVersion(appVersionOverride ?? assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly?.GetName().Version?.ToString());
        appVersion = knownAppVersion ?? "unknown";
        osVersion = TrimVersion(Environment.OSVersion.Version.ToString());
        version = versionOverride ?? "dotnet/0.2.3";
        if (version == "") version = null;
        else if (version.Length > 32 || !Wire.Version().IsMatch(version)) { Log("client version does not match ^[a-z]+/[0-9A-Za-z.+-]{1,24}$; `v` is omitted"); version = null; }
        lock (gate) metadata = new(appVersion, os, osVersion, arch, slug, version);
        storage = new Storage(stateOverride ?? Storage.DefaultDirectory(key, slug, os), Log, beforeStorageReplace);
        if (!storage.Open()) { storage.Dispose(); storage = null; Inactive("storage unavailable; SDK inactive"); return; }
        // Recovery inserts historical records before newly admitted calls in the same bounded buffer.
        state = storage.Load(events);
        events.StampMissing(metadata);
        installEnqueuedThisRun = events.Contains("install");
        CreateIdentity(now, installOrigin);
        if (!Save()) { Inactive("could not persist install claim; SDK inactive"); return; }
        if (!ObserveVersion(now)) { Inactive("could not commit app version observation; SDK inactive"); return; }
        if (state.LastHeartbeatDay != Day(now)) { state.LastHeartbeatDay = Day(now); AddHeartbeat(true); }
        initAt = now + 2000;
        QueueInstall(now, true);
        Save(); Persist(true);
    }
    private bool ObserveVersion(BigInteger now)
    {
        if (!RecoverUpdate()) return false;
        if (knownAppVersion is null || knownAppVersion == state.LastAppVersion) return true;
        var previous = state.LastAppVersion;
        state.LastAppVersion = knownAppVersion;
        if (previous is not null)
        {
            var props = Wire.Props("app_updated", new Dictionary<string, object?> { ["from_version"] = previous, ["to_version"] = knownAppVersion }, Log)!;
            state.PendingUpdate = EventRecord.Create("app_updated", Wire.Decimal(now), props, metadata: metadata);
        }
        // The atomic intent contains the complete transition before its baseline
        // advances. A failed write leaves no deliverable new transition.
        if (!Save()) { state.LastAppVersion = previous; state.PendingUpdate = null; return false; }
        return RecoverUpdate();
    }
    private bool RecoverUpdate()
    {
        if (state.PendingUpdate is not { } update) return true;
        EventRecord[] snapshot;
        lock (gate)
        {
            events.WatchEviction(update.Id);
            state.PendingUpdateDiscarded |= events.WasEvicted(update.Id);
            if (state.PendingUpdateDiscarded) events.Remove([update.Id]);
            else if (!events.ContainsId(update.Id)) Add(update, true);
            snapshot = events.Snapshot(); dirty = false;
        }
        // Capture the cap decision once. Later admissions stay dirty, and no
        // filesystem operation holds the caller's admission lock.
        var saved = storage?.CommitUpdate(state, snapshot) ?? false;
        if (saved) events.WatchEviction(null);
        if (!saved) { lock (gate) dirty = true; }
        return saved;
    }
    internal void LegacyVersion()
    {
        var saved = false;
        Invoke(() => {
            if (!accepting || storage is null || state.PendingUpdate is not null) return;
            var previous = state.LastAppVersion; state.LastAppVersion = null;
            saved = Save(); if (!saved) state.LastAppVersion = previous;
        });
        if (!saved) throw new InvalidOperationException("legacyversion requires initialized durable state without a pending transition");
    }
    private void Inactive(string reason)
    {
        Log(reason);
        storage?.Dispose(); storage = null;
        lock (gate) { accepting = initialized = false; id = ""; events.Clear(); dirty = false; }
    }
    private static string TrimVersion(string value) => value.Length <= 32 ? value : value[..32];
    private void CreateIdentity(BigInteger now, InstallOrigin origin = InstallOrigin.Unknown)
    {
        if (state.InstallId == "")
        {
            state.InstallId = Guid.NewGuid().ToString("D");
            state.InstallOrigin = origin switch { InstallOrigin.New => "new", InstallOrigin.Existing => "existing", _ => "unknown" };
        }
        state.InstallOrigin ??= "unknown";
        if (!state.InstallClaimed && state.InstallDueAt is null) state.InstallDueAt = Wire.Decimal(now);
        lock (gate) id = state.InstallId;
    }
    private void OnExit(object? sender, EventArgs args) => Terminate();
    private bool Save() => storage?.Save(state) ?? false;
    private bool Persist(bool force = false)
    {
        EventRecord[] snapshot;
        lock (gate) { if (!dirty && !force) return true; dirty = false; snapshot = events.Snapshot(); }
        var saved = storage?.Sync(snapshot, force) ?? false;
        if (!saved) { lock (gate) dirty = true; }
        return saved;
    }
    private void Post(Action action)
    {
        lock (gate)
        {
            if (!initialized || quitting) return;
            // Controls are bounded separately; only Track consumes the shared event budget.
            if (commands.Count >= 64) { Log("SDK control queue full; call dropped"); return; }
            commands.Enqueue(action); wake.Set();
        }
    }
    private void Invoke(Action action)
    {
        TaskCompletionSource complete;
        lock (gate)
        {
            while (commands.Count >= 64 && !quitting) Monitor.Wait(gate);
            if (worker is null || quitting) return;
            complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.Enqueue(() => { try { action(); } finally { complete.SetResult(); } }); wake.Set();
        }
        // Bounded: a caller must never block forever on a worker that cannot make
        // progress. A timeout leaves the caller with whatever stale/default value it
        // already had, exactly as if the action had not run yet.
        if (!complete.Task.Wait(5000)) Log("SDK worker did not respond within 5 s; call returns stale/default state");
    }
    internal void Barrier() => Safe(() => Invoke(() => Persist()));
    internal void Advance(long ms)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
        if (!ClockPinned) { Thread.Sleep(TimeSpan.FromMilliseconds(ms)); Barrier(); return; }
        Settle();
        Invoke(() => { lock (gate) clockPin += ms; });
        Settle();
    }
    private void Settle()
    {
        var deadline = Environment.TickCount64 + 30000;
        while (Environment.TickCount64 < deadline)
        {
            var settled = false;
            Invoke(() => { Tick(); settled = flight is null && !DueNow(); Persist(); });
            if (settled) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("sleep did not settle within 30 s of real time");
    }
    private bool DueNow()
    {
        if (!accepting) return false;
        var now = Now();
        BigInteger? track;
        lock (gate) track = trackAt;
        return !Gated(now) && (state.StopProbeDue || (pending && events.Size.Count > 0) || initAt <= now || track <= now);
    }
    private void Run()
    {
        try
        {
            while (true)
            {
                Action? command;
                lock (gate)
                {
                    if (quitting && commands.Count == 0) break;
                    command = commands.Count > 0 ? commands.Dequeue() : null;
                    workerBusy = true;
                    Monitor.PulseAll(gate);
                }
                try
                {
                    if (command is not null) Safe(command);
                    else Safe(() => { Persist(); Tick(); });
                }
                finally { lock (gate) workerBusy = false; }
                if (command is null) wake.WaitOne(NextDelay());
            }
        }
        // A bounded caller may return while a filesystem operation is still
        // finishing. Only its worker can release the exclusive storage lease.
        finally { storage?.Dispose(); }
    }
    private int NextDelay()
    {
        if (flight is not null) return 10;
        if (!accepting || ClockPinned) return Timeout.Infinite;
        var now = Now();
        var deadlines = new List<BigInteger?> { initAt, trackAt, Wire.Instant(state.BackoffNextAt), Wire.Instant(state.StopUntil) };
        if (!state.InstallClaimed)
        {
            if (!installEnqueuedThisRun && !events.Contains("install")) deadlines.Add(Wire.Instant(state.InstallDueAt));
            if (Wire.Instant(state.InstallFirstTry) is { } first) deadlines.Add(first + 2592000000L);
        }
        if (DueNow()) return 1;
        var next = deadlines.Where(d => d > now).Select(d => d!.Value - now).DefaultIfEmpty(60000).Min();
        return (int)BigInteger.Min(next, 60000);
    }
    private bool Gated(BigInteger now) => Wire.Instant(state.StopUntil) > now || Wire.Instant(state.BackoffNextAt) > now;
    private void Tick()
    {
        if (!accepting || storage is null) return;
        var now = Now();
        if (flight is { } completed && completed.Task.IsCompleted)
        {
            flight = null;
            if (completed.Generation == generation) Answer(completed, completed.Task.GetAwaiter().GetResult(), now);
        }
        if (!state.InstallClaimed)
        {
            if (Wire.Instant(state.InstallFirstTry) is { } first && now >= first + 2592000000L) { state.InstallClaimed = true; Save(); }
            else QueueInstall(now, false);
        }
        if (Gated(now) || flight is not null) return;
        if (state.StopUntil is not null) { state.StopUntil = null; Log("kill switch elapsed"); Save(); }
        if (initAt <= now) { initAt = null; pending = true; }
        lock (gate) { if (trackAt <= now) { trackAt = null; pending = true; } }
        if (state.StopProbeDue)
        {
            state.Probe ??= EventRecord.Create("heartbeat", Wire.Decimal(now), "{}"u8.ToArray(), true, metadata);
            Save(); Send([state.Probe], true); return;
        }
        if (!pending) return;
        if (events.Size.Count == 0) { pending = false; return; }
        Send(events.Snapshot(100), false);
    }
    private void QueueInstall(BigInteger now, bool first)
    {
        if (state.InstallClaimed || installEnqueuedThisRun || Wire.Instant(state.InstallDueAt) > now || events.Contains("install")) return;
        if (Wire.Instant(state.InstallFirstTry) is { } tried && now >= tried + 2592000000L) return;
        installEnqueuedThisRun = true;
        state.InstallFirstTry ??= Wire.Decimal(now);
        var props = Wire.Json(w => { w.WriteStartObject(); w.WriteString("install_origin", state.InstallOrigin ?? "unknown"); w.WriteEndObject(); });
        // Bootstrap is asynchronous: the initial claim belongs before calls
        // already admitted after Initialize, just like the initial heartbeat.
        Add(EventRecord.Create("install", Wire.Decimal(now), props, metadata: metadata), first);
        if (initAt is null) pending = true;
        Save();
    }
    private byte[] Render(EventRecord e) => Wire.Json(w => {
        var observed = e.Metadata ?? metadata!;
        w.WriteStartObject(); w.WriteString("id", e.Id); w.WriteString("n", e.Name);
        w.WritePropertyName("t"); w.WriteRawValue(e.Time); w.WriteString("s", "app");
        w.WriteString("iid", state.InstallId); w.WriteString("av", observed.AppVersion); w.WriteString("os", observed.OS);
        w.WriteString("osv", observed.OSVersion); w.WriteString("arch", observed.Arch);
        if (observed.App is not null) w.WriteString("a", observed.App);
        if (observed.ClientVersion is not null) w.WriteString("v", observed.ClientVersion);
        if (e.Heartbeat)
        {
            if (state.InstallProps.Count > 0) { w.WritePropertyName("props"); JsonSerializer.Serialize(w, state.InstallProps.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)); }
        }
        else
        {
            using var doc = JsonDocument.Parse(e.Data);
            var props = doc.RootElement.GetProperty("props");
            if (props.EnumerateObject().Any()) { w.WritePropertyName("props"); props.WriteTo(w); }
        }
        w.WriteEndObject();
    });
    private void Send(EventRecord[] candidates, bool probe)
    {
        if (!probe && state.RetryIds.Length > 0)
        {
            var retained = events.Snapshot().Where(e => state.RetryIds.Contains(e.Id, StringComparer.Ordinal)).ToArray();
            if (retained.Length == state.RetryIds.Length) candidates = retained;
            // Oldest-first eviction also retires any retry snapshot that retained those events.
            else { state.RetryIds = []; Save(); }
        }
        var rendered = new List<byte[]>(); var used = new List<EventRecord>();
        var count = Encoding.UTF8.GetByteCount(key) + 24;
        foreach (var e in candidates)
        {
            byte[] bytes;
            // A durable record that cannot be rendered (e.g. a pre-existing malformed `t`)
            // must never re-enter this loop unbounded; discard just that one record instead
            // of throwing, which previously left the worker retrying the same poison forever.
            try { bytes = Render(e); }
            catch { events.Remove([e.Id]); dirty = true; Log("drop event: could not render onto the wire; discarding"); continue; }
            if (count + bytes.Length + 1 > Wire.MaxBody) break;
            count += bytes.Length + 1; rendered.Add(bytes); used.Add(e);
        }
        if (used.Count == 0) { events.Remove([candidates[0].Id]); dirty = true; Log("drop event: exceeds 64 KiB batch budget"); return; }
        var body = Wire.Json(w => {
            w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("p", key); w.WriteStartArray("e");
            foreach (var bytes in rendered) w.WriteRawValue(bytes);
            w.WriteEndArray(); w.WriteEndObject();
        });
        if (body.Length > Wire.MaxBody) throw new InvalidOperationException("body budget");
        if (!probe) { state.RetryIds = used.Select(e => e.Id).ToArray(); Save(); }
        Dispatch(body, used.ToArray(), probe);
    }
    private void Dispatch(byte[] body, EventRecord[] used, bool probe)
    {
        Persist();
        lock (gate)
        {
            if (!accepting) return;
            requestCancellation?.Dispose(); requestCancellation = new();
            if (Debug) { try { Console.Error.WriteLine(Encoding.UTF8.GetString(body)); } catch { } }
            flight = new(transport.Post(endpoint, body, mock, requestCancellation.Token), used, probe, generation, body.Length);
            Account();
        }
    }
    private void Answer(Flight sent, Outcome outcome, BigInteger now)
    {
        if (outcome.Retryable)
        {
            var step = state.BackoffStepMs == 0 ? 1000 : state.BackoffStepMs;
            var wait = Math.Clamp((int)Math.Round(step * (0.8 + Random.Shared.NextDouble() * 0.4)), 1, 3600000);
            var header = outcome.RetryAfter?.Trim(' ', '\t');
            if (!string.IsNullOrEmpty(header))
            {
                if (!Wire.Seconds().IsMatch(header)) Log("Retry-After is not delay-seconds and is treated as absent");
                else
                {
                    var seconds = BigInteger.Parse(header, CultureInfo.InvariantCulture);
                    if (seconds > 3600) Log("Retry-After exceeds the 3600 s ceiling; clamped");
                    wait = Math.Max(wait, (int)BigInteger.Min(seconds, 3600) * 1000);
                }
            }
            state.BackoffStepMs = Math.Min(step * 2, 3600000); state.BackoffNextAt = Wire.Decimal(now + wait + 1);
            pending = true; Log($"retry in {wait} ms; status={outcome.Status}"); Save(); return;
        }
        if (!sent.Probe)
        {
            state.RetryIds = [];
            events.Remove(sent.Events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal)); dirty = true;
            if (outcome.Status == 202 && sent.Events.Any(e => e.Name == "install")) state.InstallClaimed = true;
        }
        state.BackoffStepMs = 0; state.BackoffNextAt = null;
        if (sent.Probe) { state.StopProbeDue = false; state.Probe = null; }
        if (outcome.Status != 202) Log($"batch dropped: status={outcome.Status}{(outcome.Status == 402 ? " payment_required" : "")}; final, not retried");
        try
        {
            using var doc = JsonDocument.Parse(outcome.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("rejected", out var rejected)) Log("events rejected: " + rejected.GetRawText());
            if (root.TryGetProperty("stop", out var stop) && stop.TryGetProperty("scope", out var scope))
            {
                if (scope.GetString() == "web") Log("ignoring a stop scoped to web");
                else if (scope.GetString() == "app" && stop.TryGetProperty("until", out var until) && Wire.Instant(until.GetRawText()) is { } seconds)
                {
                    state.StopUntil = Wire.Decimal(seconds * 1000); state.StopProbeDue = true; state.Probe = null;
                    Log("kill switch: no request until " + state.StopUntil + " ms, scope app");
                }
            }
        }
        catch { if (outcome.Body.Length > 0) Log("response body is not usable JSON"); }
        pending = events.Size.Count > 0; Save(); Persist();
    }
    private int Account()
    {
        var size = events.Size;
        // Payload arrays + record/list/string overhead + bounded control queue, state, persistence
        // buffers, response read/parse and one in-flight request. Framework arenas are excluded.
        var total = size.Bytes + size.Count * 384 + commands.Count * 4096 + 384 * 1024;
        peakAccounted = Math.Max(peakAccounted, total);
        if (total > 2 * 1048576) throw new InvalidOperationException("SDK state budget");
        return total;
    }
    internal string Export()
    {
        string result = "{}";
        Invoke(() => result = ExportCore());
        return worker is null ? ExportCore() : result;
    }
    private string ExportCore()
    {
        var queue = events.Snapshot();
        return JsonSerializer.Serialize(new {
            install_id = id, last_heartbeat_day = state.LastHeartbeatDay, last_app_version = state.LastAppVersion,
            install_origin = state.InstallOrigin,
            install_claimed = state.InstallClaimed, install_due_at = state.InstallDueAt,
            install_first_try = state.InstallFirstTry, install_props = state.InstallProps,
            backoff_step_ms = state.BackoffStepMs, backoff_next_at = state.BackoffNextAt,
            stop_until = state.StopUntil, stop_probe_due = state.StopProbeDue,
            accounted_state_bytes = Account(), peak_accounted_state_bytes = peakAccounted,
            queue = new { bytes = events.Size.Bytes, events = queue.Select(e => new { id = e.Id, n = e.Name, t = e.Time }) }
        });
    }
    internal void Terminate() => Safe(() => {
        lock (gate)
        {
            if (worker is null || quitting) return;
            commands.Enqueue(() => pending = true); wake.Set();
        }
        // ProcessExit must remain bounded even with a blocked filesystem or active request,
        // but idle termination (nothing queued, no flight, nothing dirty) returns early.
        var deadline = Environment.TickCount64 + 550;
        while (Environment.TickCount64 < deadline && !Idle()) Thread.Sleep(5);
        lock (gate)
        {
            quitting = true; accepting = false; generation++; requestCancellation?.Cancel();
            // The worker completes queued commands before exiting. Running them
            // here would race its current filesystem operation and could block
            // ProcessExit beyond the shutdown budget.
            wake.Set(); Monitor.PulseAll(gate);
        }
        if (worker?.IsAlive == true) worker.Join(25);
        if (exitRegistered) { AppDomain.CurrentDomain.ProcessExit -= OnExit; exitRegistered = false; }
    });
    // dirty clears when a snapshot is taken, before that snapshot reaches disk.
    // workerBusy also covers acknowledgement compaction and dequeued controls;
    // pending covers a forced flush or a retry scheduled by a failed request.
    private bool Idle() { lock (gate) return !workerBusy && commands.Count == 0 && flight is null && !dirty && !pending; }
    public void Dispose() { Terminate(); transport.Dispose(); }
    private sealed record Flight(Task<Outcome> Task, EventRecord[] Events, bool Probe, long Generation, int Bytes);
}
