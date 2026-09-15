using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jelto;

internal sealed class State
{
    public string InstallId { get; set; } = "";
    public string? InstallOrigin { get; set; }
    public string? LastAppVersion { get; set; }
    public EventRecord? PendingUpdate { get; set; }
    public bool PendingUpdateDiscarded { get; set; }
    public string? LastHeartbeatDay { get; set; }
    public bool InstallClaimed { get; set; }
    public string? InstallDueAt { get; set; }
    public string? InstallFirstTry { get; set; }
    public Dictionary<string, string> InstallProps { get; set; } = new(StringComparer.Ordinal);
    public int BackoffStepMs { get; set; }
    public string? BackoffNextAt { get; set; }
    public string? StopUntil { get; set; }
    public bool StopProbeDue { get; set; }
    public EventRecord? Probe { get; set; }
    public string[] RetryIds { get; set; } = [];
}

internal sealed record EventMetadata(string AppVersion, string OS, string OSVersion, string Arch, string? App, string? ClientVersion);

internal sealed record EventRecord(string Id, string Name, string Time, byte[] Data, bool Heartbeat = false, bool Historical = false, EventMetadata? Metadata = null)
{
    internal static EventRecord Create(string name, string time, byte[] props, bool heartbeat = false, EventMetadata? metadata = null)
    {
        var id = Guid.NewGuid().ToString("D");
        return Encode(id, name, time, props, heartbeat, metadata);
    }
    private static EventRecord Encode(string id, string name, string time, byte[] props, bool heartbeat, EventMetadata? metadata)
    {
        var bytes = Wire.Json(w => {
            w.WriteStartObject(); w.WriteString("id", id); w.WriteString("n", name); w.WriteString("t", time);
            if (heartbeat) w.WriteBoolean("hb", true);
            if (metadata is not null) { w.WritePropertyName("meta"); JsonSerializer.Serialize(w, metadata); }
            w.WritePropertyName("props"); w.WriteRawValue(props); w.WriteEndObject();
        });
        return new(id, name, time, bytes, heartbeat, Metadata: metadata);
    }
    internal EventRecord WithMetadata(EventMetadata metadata)
    {
        using var doc = JsonDocument.Parse(Data);
        return Encode(Id, Name, Time, Encoding.UTF8.GetBytes(doc.RootElement.GetProperty("props").GetRawText()), Heartbeat, metadata) with { Historical = Historical };
    }
    internal static EventRecord? Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var name = root.GetProperty("n").GetString()!;
        var rawTime = root.GetProperty("t").GetString()!;
        if (!Guid.TryParseExact(id, "D", out var uuid) || uuid == Guid.Empty || !Wire.Name().IsMatch(name) || Wire.Instant(rawTime) is not { } instant) return null;
        // Re-canonicalize rather than trust the disk text verbatim: WriteRawValue on the wire
        // later requires exact JSON-number grammar, and BigInteger round-tripping (e.g. "-0")
        // does not guarantee that on its own even once Wire.Integer() rejects leading zeros.
        var time = Wire.Decimal(instant);
        var props = root.GetProperty("props").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        if (Wire.Props(name, props, _ => { }) is null) return null;
        EventMetadata? metadata = root.TryGetProperty("meta", out var meta) ? meta.Deserialize<EventMetadata>() : null;
        if (metadata is not null && (Wire.KnownAppVersion(metadata.AppVersion) is null || metadata.OS is not ("macos" or "windows" or "linux") || metadata.Arch is not ("arm64" or "x64" or "x86") || metadata.OSVersion is null || metadata.OSVersion.EnumerateRunes().Count() > 32 || (metadata.App is not null && !Wire.Slug().IsMatch(metadata.App)))) return null;
        return new(id, name, time, bytes, root.TryGetProperty("hb", out var hb) && hb.ValueKind == JsonValueKind.True, true, metadata);
    }
}

// One buffer is also the worker's bounded inbox. Admission only adds immutable records;
// lifecycle, persistence, clocks and network outcomes are exclusively owned by the worker.
internal sealed class EventBuffer
{
    internal const int MaxEvents = 1000, MaxBytes = 1048576;
    private readonly LinkedList<EventRecord> events = new();
    private readonly object gate = new();
    private int bytes;
    private string? watchedId;
    private bool watchedEvicted;
    internal (int Count, int Bytes) Size { get { lock (gate) return (events.Count, bytes); } }
    internal int Add(EventRecord e, bool first = false)
    {
        lock (gate)
        {
            if (first || e.Historical)
            {
                var node = events.First;
                while (node is not null && node.Value.Historical) node = node.Next;
                if (node is null) events.AddLast(e); else events.AddBefore(node, e with { Historical = true });
            }
            else events.AddLast(e);
            bytes += e.Data.Length + 1;
            var dropped = 0;
            while (events.Count > MaxEvents || bytes > MaxBytes)
            {
                DropFirst(); dropped++;
            }
            return dropped;
        }
    }
    internal EventRecord[] Snapshot(int max = MaxEvents) { lock (gate) return events.Take(max).ToArray(); }
    internal bool Contains(string name) { lock (gate) return events.Any(e => e.Name == name); }
    internal bool ContainsId(string id) { lock (gate) return events.Any(e => e.Id == id); }
    internal void WatchEviction(string? id)
    {
        lock (gate) { if (watchedId != id) { watchedId = id; watchedEvicted = false; } }
    }
    internal bool WasEvicted(string id) { lock (gate) return watchedId == id && watchedEvicted; }
    private void DropFirst()
    {
        var first = events.First!.Value;
        if (first.Id == watchedId) watchedEvicted = true;
        bytes -= first.Data.Length + 1; events.RemoveFirst();
    }
    internal void StampMissing(EventMetadata metadata)
    {
        lock (gate)
        {
            foreach (var node in Nodes())
            {
                if (node.Value.Metadata is not null) continue;
                bytes -= node.Value.Data.Length + 1;
                node.Value = node.Value.WithMetadata(metadata);
                bytes += node.Value.Data.Length + 1;
            }
            while (bytes > MaxBytes) DropFirst();
        }
    }
    private IEnumerable<LinkedListNode<EventRecord>> Nodes() { for (var node = events.First; node is not null; node = node.Next) yield return node; }
    internal void Remove(HashSet<string> ids)
    {
        lock (gate)
        {
            var node = events.First;
            while (node is not null)
            {
                var next = node.Next;
                if (ids.Contains(node.Value.Id)) { bytes -= node.Value.Data.Length + 1; events.Remove(node); }
                node = next;
            }
        }
    }
    internal void Clear() { lock (gate) { events.Clear(); bytes = 0; watchedId = null; watchedEvicted = false; } }
}

internal sealed class Storage(string directory, Action<string> log) : IDisposable
{
    // POSIX-only: the state directory is 0700 and every file within it is 0600, re-asserted
    // even when the directory or a file already existed (an upgrade from a looser mode).
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeRW = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private FileStream? lease;
    private readonly HashSet<string> persisted = new(StringComparer.Ordinal);
    private long journalBytes;
    internal string DirectoryPath => directory;
    internal string StatePath => Path.Combine(directory, "state.json");
    internal string QueuePath => Path.Combine(directory, "queue.jsonl");
    internal static string DefaultDirectory(string key, string? slug, string os)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) throw new IOException("local application data unavailable");
        var assembly = Assembly.GetEntryAssembly()?.GetName().Name ?? "desktop";
        var component = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assembly))).ToLowerInvariant()[..24];
        return Path.Combine(root, "Jelto", component, key, slug ?? os);
    }
    private static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeRW;
        return new FileStream(path, options);
    }
    internal bool Open()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory, DirMode);
                // Re-assert the mode even on a pre-existing directory, so an upgrade from an
                // earlier, looser SDK version closes the exposure on the next launch.
                File.SetUnixFileMode(directory, DirMode);
                foreach (var name in new[] { "writer.lock", "state.json", "queue.jsonl" })
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path)) File.SetUnixFileMode(path, FileModeRW);
                }
            }
            else Directory.CreateDirectory(directory);
            lease = OpenFile(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch { log("storage unavailable or already in use; SDK inactive"); return false; }
    }
    internal State Load(EventBuffer queue)
    {
        State state;
        try
        {
            if (new FileInfo(StatePath).Length > 131072) throw new IOException();
            state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(StatePath)) ?? new();
            if (!Guid.TryParseExact(state.InstallId, "D", out var id) || id == Guid.Empty || state.InstallId[14] != '4') throw new JsonException();
            if (state.InstallOrigin is not ("new" or "existing" or "unknown")) state.InstallOrigin = "unknown";
            if (state.InstallProps is null || state.InstallProps.Count > 20 || state.InstallProps.Any(p => !Wire.PropKey().IsMatch(p.Key) || p.Value is null || !Wire.InstallValue().IsMatch(p.Value))) throw new JsonException();
            state.InstallProps.Remove("install_origin");
            foreach (var instant in new[] { state.LastHeartbeatDay, state.InstallDueAt, state.InstallFirstTry, state.BackoffNextAt, state.StopUntil })
                if (instant is not null && Wire.Instant(instant) is null) throw new JsonException();
            if (state.BackoffStepMs is < 0 or > 3600000) throw new JsonException();
            if (state.Probe is not null) state.Probe = EventRecord.Parse(state.Probe.Data);
            if (state.LastAppVersion is not null && Wire.KnownAppVersion(state.LastAppVersion) is null) state.LastAppVersion = null;
            if (state.PendingUpdate is not null)
            {
                state.PendingUpdate = EventRecord.Parse(state.PendingUpdate.Data);
                if (state.PendingUpdate is null || state.PendingUpdate.Name != "app_updated" || state.PendingUpdate.Metadata?.AppVersion != state.LastAppVersion) throw new JsonException();
            }
            if (state.RetryIds is null || state.RetryIds.Length > 100 || state.RetryIds.Any(id => !Guid.TryParseExact(id, "D", out _))) throw new JsonException();
        }
        catch { state = new(); log("state missing or corrupt; creating a fresh install identity"); }
        // A replayed intent may be evicted by calls already admitted during init.
        // Remember that cap decision until recovery can durably retire the intent.
        queue.WatchEviction(state.PendingUpdate?.Id);
        // A queue without a recoverable identity must never be reassigned to a new install.
        if (state.InstallId != "")
        {
            try
            {
                using var input = new FileStream(QueuePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Stream a bounded suffix. Compaction normally keeps the log below 2 MiB.
                if (input.Length > 2 * EventBuffer.MaxBytes) { input.Seek(-2 * EventBuffer.MaxBytes, SeekOrigin.End); SkipLine(input); }
                var line = new byte[Wire.MaxBody]; var length = 0; var oversized = false;
                int b;
                while ((b = input.ReadByte()) >= 0)
                {
                    if (b == '\n')
                    {
                        if (!oversized && length > 0)
                        {
                            try { var e = EventRecord.Parse(line.AsSpan(0, length).ToArray()); if (e is not null && !(state.PendingUpdateDiscarded && e.Id == state.PendingUpdate?.Id)) queue.Add(e); }
                            catch { log("discarding corrupt queue record"); }
                        }
                        length = 0; oversized = false;
                    }
                    else if (length < line.Length) line[length++] = (byte)b;
                    else oversized = true;
                }
                // No newline means an interrupted append, even if the partial JSON parses.
            }
            catch { log("queue unavailable; continuing with recovered state"); }
        }
        // Intent recovery owns its queue checkpoint. Compacting here could
        // commit a cap discard while a live intent can still restore that ID.
        if (state.PendingUpdate is null) Sync(queue.Snapshot(), true);
        return state;
    }
    private static void SkipLine(Stream stream) { int b; while ((b = stream.ReadByte()) >= 0 && b != '\n') { } }
    internal bool Save(State state)
    {
        try { Atomic(StatePath, stream => JsonSerializer.Serialize(stream, state)); return true; }
        catch { log("could not persist SDK state"); return false; }
    }
    internal bool CommitUpdate(State state, EventRecord[] snapshot)
    {
        if (state.PendingUpdate is not { } update) return false;
        var discarded = state.PendingUpdateDiscarded || !snapshot.Any(e => e.Id == update.Id);
        if (discarded)
        {
            var previous = state.PendingUpdateDiscarded;
            state.PendingUpdateDiscarded = true;
            if (!Save(state)) { state.PendingUpdateDiscarded = previous; return false; }
            snapshot = snapshot.Where(e => e.Id != update.Id).ToArray();
        }
        if (!Sync(snapshot, true)) return false;
        state.PendingUpdate = null;
        state.PendingUpdateDiscarded = false;
        if (!Save(state)) { state.PendingUpdate = update; state.PendingUpdateDiscarded = discarded; return false; }
        return true;
    }
    internal bool Sync(EventRecord[] queue, bool compact = false)
    {
        try
        {
            // Removal is a compaction, so an acknowledged/evicted event cannot reappear on restart.
            if (compact || persisted.Count > queue.Length || !persisted.IsSubsetOf(queue.Select(e => e.Id).ToHashSet(StringComparer.Ordinal)) || journalBytes > 2 * EventBuffer.MaxBytes)
            {
                Atomic(QueuePath, stream => { foreach (var e in queue) { stream.Write(e.Data); stream.WriteByte((byte)'\n'); } });
                persisted.Clear(); journalBytes = 0;
                foreach (var e in queue) { persisted.Add(e.Id); journalBytes += e.Data.Length + 1; }
                return true;
            }
            using var output = OpenFile(QueuePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (var e in queue)
            {
                if (persisted.Contains(e.Id)) continue;
                output.Write(e.Data); output.WriteByte((byte)'\n'); journalBytes += e.Data.Length + 1;
            }
            output.Flush(true);
            foreach (var e in queue) persisted.Add(e.Id);
            return true;
        }
        catch { log("could not persist event queue; retaining bounded memory queue"); return false; }
    }
    private static void Atomic(string path, Action<FileStream> write)
    {
        var temp = path + ".tmp";
        // Delete rather than truncate: a planted symlink is unlinked, not followed, so its
        // target is never touched. FileMode.CreateNew then refuses to reuse anything left
        // behind by a failed delete (e.g. an obstacle directory), preserving the failure path.
        try { File.Delete(temp); } catch { }
        using (var stream = OpenFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    internal void Wipe()
    {
        Dispose(); persisted.Clear(); journalBytes = 0;
        foreach (var name in new[] { "queue.jsonl", "queue.jsonl.tmp", "state.json", "state.json.tmp", "writer.lock" })
        {
            try { File.Delete(Path.Combine(directory, name)); }
            catch { log("could not remove SDK state file"); }
        }
    }
    public void Dispose() { lease?.Dispose(); lease = null; }
}
