using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Jelto;

const string Key = "prd_conform001";
const string Pin = "1788134400000";
if (args.Contains("--exit-child"))
{
    JeltoClient.Initialize(Key);
    return 0;
}
var failures = 0;
var tests = new (string Name, Action Run)[] {
    ("pre-init has no storage or identity", () => {
        using var box = new Box(); box.Sdk.Track("x"); box.Sdk.SetProps(new Dictionary<string,string>{{"license","paid"}});
        Check(box.Sdk.InstallId == "", "pre-init id"); Check(Events(box.Sdk).Length == 0, "pre-init queue");
        Check(!Directory.Exists(box.Path), "pre-init writes");
    }),
    ("lifecycle, persistent identity, daily heartbeat and reset", () => {
        using var server = new Server();
        using var box = new Box(server.Url, Pin); box.Sdk.Initialize(Key, "desktop");
        var id = box.Sdk.InstallId; Check(Guid.Parse(id).Version() == 4, "UUIDv4");
        box.Sdk.Advance(3000); Check(server.Requests.Count() == 1, "initial flush");
        Check(server.Requests.First().GetProperty("e")[0].GetProperty("a").GetString() == "desktop", "slug");
        box.Sdk.SetProps(new Dictionary<string,string>{{"license","paid"}}); box.Sdk.Advance(0);
        box.Sdk.Track("old_identity"); box.Sdk.Reset();
        Check(box.Sdk.InstallId != id, "reset rotates"); Check(!Events(box.Sdk).Any(e => e.GetProperty("n").GetString() == "old_identity"), "reset clears");
        Check(State(box.Sdk).GetProperty("install_props").GetProperty("license").GetString() == "paid", "reset keeps properties");
        var rotated = box.Sdk.InstallId; box.Sdk.Dispose();
        using var resumed = new Engine(box.Path, server.Url, Pin); resumed.Initialize(Key); Check(resumed.InstallId == rotated, "restart identity");
        resumed.Advance(3000); Check(State(resumed).GetProperty("last_heartbeat_day").GetString() == Wire.Decimal(BigInteger.Parse(Pin) / 86400000), "day index");
    }),
    ("disable during initialization cannot restore identity", () => {
        for (var i = 0; i < 5; i++)
        {
            using var box = new Box(pin: Pin);
            box.Sdk.Initialize(Key);
            box.Sdk.Disable();
            Check(box.Sdk.InstallId == "", "bootstrap restored disabled identity");
            Check(!Directory.Exists(box.Path) || !Directory.EnumerateFileSystemEntries(box.Path).Any(), "bootstrap restored disabled files");
        }
    }),
    ("immediate track after reinitialization uses the new app metadata", () => {
        using var server = new Server(); using var box = new Box(server.Url, Pin, appVersion: "release A");
        box.Sdk.Initialize(Key, "first"); _ = box.Sdk.InstallId; box.Sdk.Disable();
        // Hold admission so the worker cannot bootstrap until both public calls finish.
        lock (AdmissionGate(box.Sdk)) {
            box.Sdk.Initialize(Key, "second"); box.Sdk.Track("reopened");
        }
        box.Sdk.Advance(6000);
        var reopened = server.Requests.SelectMany(r => r.GetProperty("e").EnumerateArray()).Single(e => e.GetProperty("n").GetString() == "reopened");
        Check(reopened.GetProperty("a").GetString() == "second", "track retained metadata from the disabled app");
    }),
    ("immediate process exit invokes the public SDK's bounded flush", () => {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jelto-exit-test-" + Guid.NewGuid());
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(JeltoClient).Assembly.Location.Replace("Jelto.dll", "Jelto.Tests.dll"));
            start.ArgumentList.Add("--exit-child");
            start.Environment["JELTO_STATE_DIR"] = path;
            start.Environment["JELTO_ENDPOINT"] = "http://127.0.0.1:1/v1/e";
            start.Environment["JELTO_NOW"] = Pin;
            start.Environment["JELTO_DEBUG"] = "0";
            using var child = Process.Start(start)!;
            if (!child.WaitForExit(2000)) { child.Kill(); throw new Exception("process-exit handler exceeded its budget"); }
            Check(child.ExitCode == 0, "process-exit failure: " + child.StandardError.ReadToEnd());
            Check(File.Exists(System.IO.Path.Combine(path, "state.json")), "immediate exit lost initialization");
            Check(child.StandardOutput.ReadToEnd() == "" && child.StandardError.ReadToEnd() == "", "SDK printed without debug");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }),
    ("validation and culture-independent exact numbers", () => {
        using var server = new Server(); using var box = new Box(server.Url, Pin); box.Sdk.Initialize(Key);
        var culture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            box.Sdk.Track("numbers", new Dictionary<string,object?>{{"decimal",29.90m},{"double",1.25},{"integer",BigInteger.Parse("9223372036854775808123")},{"truth",true}});
            using var raw = JsonDocument.Parse("{\"raw\":29.90,\"large\":1e999}");
            box.Sdk.Track("raw", raw.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));
        } finally { CultureInfo.CurrentCulture = culture; }
        box.Sdk.Track("Bad Name!");
        foreach (var invalid in new object?[]{null, double.NaN, double.PositiveInfinity, new[]{1}, new Dictionary<string,string>()})
            box.Sdk.Track("invalid", new Dictionary<string,object?>{{"x",invalid}});
        box.Sdk.Track("long", new Dictionary<string,object?>{{"x",new string('a',201)}});
        box.Sdk.Track("many", Enumerable.Range(0,21).ToDictionary(i=>"k"+i, i=>(object?)i));
        box.Sdk.Track("purchase"); box.Sdk.Onboarding("bad step", "ok", null); box.Sdk.Track("onboarding:ok", new Dictionary<string,object?>{{"status","bad"}});
        box.Sdk.Onboarding("permissions", "fail", "no_driver");
        box.Sdk.Advance(6000);
        var names = server.Requests.SelectMany(r=>r.GetProperty("e").EnumerateArray()).Select(e=>e.GetProperty("n").GetString()).ToArray();
        Check(names.SequenceEqual(new[]{"heartbeat","numbers","raw","onboarding:permissions"}), "invalid values leaked: "+string.Join(',',names));
        var payload = string.Join('\n',server.Bodies); Check(payload.Contains("29.90") && payload.Contains("9223372036854775808123") && payload.Contains("1e999"), "numbers rounded");
    }),
    ("BigInteger clock including negative UTC floor", () => {
        using var server = new Server(); using var box = new Box(server.Url, "-1"); box.Sdk.Initialize(Key);
        Check(State(box.Sdk).GetProperty("last_heartbeat_day").GetString() == "-1", "negative day must floor");
        box.Sdk.Advance(3000); Check(server.Requests.First().GetProperty("e")[0].GetProperty("t").GetRawText() == "-1", "negative t corrected");
        using var far = new Box(server.Url,"922337203685477580812345"); far.Sdk.Initialize(Key); far.Sdk.Advance(3000);
        Check(server.Bodies.Last().Contains("922337203685477580812345"), "clock narrowed");
    }),
    ("shared pending/queued caps and concurrent calls", () => {
        using var box = new Box(pin: Pin); box.Sdk.Initialize(Key); _ = box.Sdk.InstallId;
        Parallel.For(0,10000,i=>box.Sdk.Track("x"+i));
        var small = State(box.Sdk); Check(small.GetProperty("queue").GetProperty("events").GetArrayLength()==1000,"event cap");
        var props = Enumerable.Range(0,20).ToDictionary(i=>"k"+i, i=>(object?)new string('a',200));
        for(var i=0;i<1500;i++) box.Sdk.Track("large"+i,props);
        var state = State(box.Sdk); var queue = state.GetProperty("queue");
        Check(queue.GetProperty("bytes").GetInt32()<=1048576,"byte cap");
        Check(queue.GetProperty("events").GetArrayLength()<1000,"byte cap did not bind");
        Check(queue.GetProperty("events").EnumerateArray().Last().GetProperty("n").GetString()=="large1499","newest missing");
        Check(state.GetProperty("peak_accounted_state_bytes").GetInt32()<=2097152,"state budget");
    }),
    ("corrupt journal and atomic-state recovery", () => {
        using var box = new Box(pin:Pin); box.Sdk.Initialize(Key); var id=box.Sdk.InstallId; box.Sdk.Track("retained"); box.Sdk.Barrier(); box.Sdk.Dispose();
        File.AppendAllText(System.IO.Path.Combine(box.Path,"queue.jsonl"),"invalid-json\n{\"partial\":");
        File.WriteAllText(System.IO.Path.Combine(box.Path,"state.json.tmp"),"interrupted replacement");
        using var restarted=new Engine(box.Path,"http://127.0.0.1:1/v1/e",Pin); restarted.Initialize(Key);
        Check(restarted.InstallId==id,"atomic state replaced by temp"); Check(Events(restarted).Any(e=>e.GetProperty("n").GetString()=="retained"),"journal lost valid prefix");
        restarted.Disable(); Check(!Directory.EnumerateFileSystemEntries(box.Path).Any(),"disable left recovery files");
    }),
    ("corrupt identity cannot reassign old queued events", () => {
        using var box = new Box(pin:Pin); box.Sdk.Initialize(Key); var id=box.Sdk.InstallId; box.Sdk.Track("old"); box.Sdk.Barrier(); box.Sdk.Dispose();
        File.WriteAllText(System.IO.Path.Combine(box.Path,"state.json"),"{}");
        using var resumed = new Engine(box.Path,"http://127.0.0.1:1/v1/e",Pin); resumed.Initialize(Key);
        Check(resumed.InstallId != id,"corrupt id retained"); Check(!Events(resumed).Any(e=>e.GetProperty("n").GetString()=="old"),"old queue reassigned");
    }),
    ("unwritable path, unsupported host and competing writer stay inactive", () => {
        var path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),Guid.NewGuid().ToString()); File.WriteAllText(path,"occupied");
        try { using var sdk=new Engine(path,"http://127.0.0.1:1",Pin); sdk.Initialize(Key); Check(sdk.InstallId=="","unwritable active"); sdk.Track("x"); } finally { File.Delete(path); }
        using var unsupported=new Box(platform:"android"); unsupported.Sdk.Initialize(Key); Check(unsupported.Sdk.InstallId=="","unsupported active"); Check(!Directory.Exists(unsupported.Path),"unsupported stored state");
        using var first = new Box(pin:Pin); first.Sdk.Initialize(Key); var id=first.Sdk.InstallId;
        using var second = new Engine(first.Path,"http://127.0.0.1:1",Pin); second.Initialize(Key); Check(second.InstallId=="","competing writer active");
        Check(first.Sdk.InstallId==id,"competing writer changed identity");
    }),
    ("POSIX directory and file modes are 0700/0600, reasserted on an existing directory", () => {
        if (OperatingSystem.IsWindows()) return;
        using var box = new Box(pin: Pin); box.Sdk.Initialize(Key); box.Sdk.Track("x"); box.Sdk.Advance(3000);
        var mask = UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute;
        Check(File.GetUnixFileMode(box.Path) == (UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute), "state dir not 0700");
        foreach (var file in Directory.EnumerateFiles(box.Path))
            Check((File.GetUnixFileMode(file) & mask) == 0, "file has group/other bits: " + file);
        box.Sdk.Dispose();
        // Loosen an existing directory and file, then confirm Open() re-asserts the mode
        // rather than trusting whatever an earlier, looser SDK version left behind.
        File.SetUnixFileMode(box.Path, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.OtherRead);
        var statePath = System.IO.Path.Combine(box.Path, "state.json");
        File.SetUnixFileMode(statePath, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead);
        using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin); resumed.Initialize(Key); _ = resumed.InstallId;
        Check(File.GetUnixFileMode(box.Path) == (UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute), "reused dir mode not reasserted");
        Check((File.GetUnixFileMode(statePath) & mask) == 0, "reused state.json mode not reasserted");
    }),
    ("a planted temp-path symlink is unlinked rather than followed", () => {
        if (OperatingSystem.IsWindows()) return;
        using var box = new Box(pin: Pin); box.Sdk.Initialize(Key); box.Sdk.Track("seed"); box.Sdk.Dispose();
        var victim = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jelto-symlink-victim-" + Guid.NewGuid());
        File.WriteAllText(victim, "precious");
        try
        {
            var tmp = System.IO.Path.Combine(box.Path, "state.json.tmp");
            File.CreateSymbolicLink(tmp, victim);
            using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin); resumed.Initialize(Key);
            resumed.Track("after-resume"); resumed.Barrier();
            Check(File.ReadAllText(victim) == "precious", "planted symlink victim was overwritten");
            // Atomic() renames the temp file onto state.json on success, so the temp path
            // itself is gone; what matters is that it is never left as (or backed by) the
            // planted symlink.
            Check(!File.Exists(tmp) && (!File.Exists(System.IO.Path.Combine(box.Path, "state.json")) || (File.GetAttributes(System.IO.Path.Combine(box.Path, "state.json")) & FileAttributes.ReparsePoint) == 0), "temp path is still a symlink after an atomic write");
        }
        finally { File.Delete(victim); }
    }),
    ("a durable poisoned `t` cannot wedge the worker; wire values stay canonical", () => {
        using var box = new Box(pin: Pin); box.Sdk.Initialize(Key); box.Sdk.Track("poison"); box.Sdk.Barrier(); box.Sdk.Dispose();
        var queuePath = System.IO.Path.Combine(box.Path, "queue.jsonl");
        var lines = File.ReadAllLines(queuePath);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains("\"n\":\"poison\"")) lines[i] = lines[i].Replace("\"t\":\"" + Pin + "\"", "\"t\":\"0" + Pin + "\"");
        File.WriteAllLines(queuePath, lines);
        using var server = new Server();
        using var resumed = new Engine(box.Path, server.Url, Pin); resumed.Initialize(Key); resumed.Advance(6000);
        Check(server.Requests.Any(), "no request arrived after a durable poisoned t");
        Check(server.Requests.SelectMany(r => r.GetProperty("e").EnumerateArray()).All(e => e.GetProperty("t").GetRawText() == Pin), "a poisoned t leaked onto the wire");
    }),
    ("batch limits and retries retain IDs and timestamps", () => {
        using var server=new Server { Status=503, RetryAfter="3" }; using var box=new Box(server.Url,Pin); box.Sdk.Initialize(Key); box.Sdk.Track("x"); box.Sdk.Advance(6000);
        var first=server.Bodies.First(); var state=State(box.Sdk); Check(state.GetProperty("backoff_step_ms").GetInt32()==2000,"backoff did not advance");
        box.Sdk.Advance(3000); Check(server.Bodies.Count==1,"Retry-After floor"); box.Sdk.Advance(1); Check(server.Bodies.Last()==first,"retry altered batch");
        server.Status=202; box.Sdk.Advance(5000); Check(Events(box.Sdk).Length==0,"accepted batch retained");
        var props=Enumerable.Range(0,20).ToDictionary(i=>"k"+i,i=>(object?)new string('z',200));
        for(var i=0;i<250;i++)box.Sdk.Track("large",props); box.Sdk.Advance(6000);
        Check(server.Bodies.All(b=>Encoding.UTF8.GetByteCount(b)<=65536),"body cap");
        Check(server.Requests.All(r=>r.GetProperty("e").GetArrayLength()<=100),"batch count cap");
    }),
    ("final responses, redirects and cookies", () => {
        using var server=new Server{Status=302,Redirect="http://127.0.0.1:1/leak"}; using var box=new Box(server.Url,Pin); box.Sdk.Initialize(Key); box.Sdk.Track("x"); box.Sdk.Advance(6000);
        Check(Events(box.Sdk).Length==0,"redirect was retried"); Check(server.Bodies.Count==1,"redirect followed");
        foreach(var status in new[]{400,402,500,202}) {
            server.Status=status; server.Body="malformed"; box.Sdk.Track("status"+status); box.Sdk.Advance(6000);
            Check(Events(box.Sdk).Length==0,"final status retained");
        }
        server.Body=new string('x',200000); box.Sdk.Track("oversized"); box.Sdk.Advance(6000); Check(Events(box.Sdk).Length==0,"oversized 202 retained");
        Check(server.Cookies.All(string.IsNullOrEmpty),"automatic cookies enabled");
    }),
    ("final install refusal is not re-enqueued in a loop", () => {
        using var server = new Server { Status = 400 };
        using var box = new Box(server.Url, Pin);
        box.Sdk.Initialize(Key); box.Sdk.Advance(25200000);
        box.Sdk.Advance(6000);
        Check(server.Requests.SelectMany(r => r.GetProperty("e").EnumerateArray()).Count(e => e.GetProperty("n").GetString() == "install") == 1, "final install was retried");
        Check(!State(box.Sdk).GetProperty("install_claimed").GetBoolean(), "400 claimed an install");
    }),
    ("stop probe persists and drains alone after a restart", () => {
        using var server=new Server{Body="{\"stop\":{\"until\":1788134460,\"scope\":\"app\"}}"};
        using var box=new Box(server.Url,Pin); box.Sdk.Initialize(Key); box.Sdk.Track("x"); box.Sdk.Advance(6000); box.Sdk.Track("backlog"); box.Sdk.Advance(30000);
        Check(server.Bodies.Count==1,"sent during stop"); box.Sdk.Dispose();
        server.Body="{}"; using var resumed=new Engine(box.Path,server.Url,"1788134436000"); resumed.Initialize(Key); _=resumed.InstallId;
        resumed.Advance(24001);
        Check(server.Requests.ElementAt(1).GetProperty("e").GetArrayLength()==1,"probe mixed with queue");
        Check(server.Requests.ElementAt(1).GetProperty("e")[0].GetProperty("n").GetString()=="heartbeat","not heartbeat probe");
        Check(server.Requests.Last().GetProperty("e")[0].GetProperty("n").GetString()=="backlog","backlog not drained");
    }),
    ("disable cancels active request; stale response cannot recreate files", () => {
        using var server=new Server{DelayMs=2000,Body="{\"stop\":{\"until\":2000000000,\"scope\":\"app\"}}"};
        using var box=new Box(server.Url); box.Sdk.Initialize(Key); box.Sdk.SetProps(new Dictionary<string,string>{{"license","paid"}});
        Wait(()=>server.Bodies.Count==1,3000); var watch=Stopwatch.StartNew(); box.Sdk.Disable();
        Check(watch.ElapsedMilliseconds<500,"disable waited for network"); Check(box.Sdk.InstallId=="","disable id");
        box.Sdk.Track("ignored"); Thread.Sleep(2200); Check(!Directory.EnumerateFileSystemEntries(box.Path).Any(),"stale response rewrote state");
        server.DelayMs=0;server.Body="{}";box.Sdk.Initialize(Key); Check(box.Sdk.InstallId!="","cannot reinitialize");
        Check(State(box.Sdk).GetProperty("install_props").EnumerateObject().Count()==0,"disabled props returned");
    }),
    ("five-second timeout and bounded termination", () => {
        using var server=new Server{DelayMs=10000}; using var box=new Box(server.Url); box.Sdk.Initialize(Key); box.Sdk.SetProps(new Dictionary<string,string>{{"license","paid"}});
        Wait(()=>server.Bodies.Count==1,3000); var watch=Stopwatch.StartNew();
        Wait(()=>State(box.Sdk).GetProperty("backoff_step_ms").GetInt32()>0,6500);
        Check(watch.ElapsedMilliseconds is >=4500 and <6000,"request timeout");
        watch.Restart();box.Sdk.Terminate();Check(watch.ElapsedMilliseconds<650,"termination budget");
    }),
    ("Initialize starts its own worker thread directly, unblocked by a saturated ThreadPool", () => {
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(1, 1);
        var release = new ManualResetEventSlim(false);
        var saturate = Math.Max(Environment.ProcessorCount * 2, 4);
        for (var i = 0; i < saturate; i++) ThreadPool.QueueUserWorkItem(_ => release.Wait());
        try
        {
            using var box = new Box(pin: Pin); var watch = Stopwatch.StartNew();
            box.Sdk.Initialize(Key); var id = box.Sdk.InstallId;
            Check(watch.ElapsedMilliseconds < 500, "InstallId blocked behind a saturated ThreadPool: " + watch.ElapsedMilliseconds + " ms");
            Check(Guid.Parse(id).Version() == 4, "UUIDv4 missing under a saturated ThreadPool");
        }
        finally { release.Set(); ThreadPool.SetMinThreads(minWorker, minIo); }
    }),
    ("idle termination does not sleep its whole shutdown budget", () => {
        using var server = new Server(); using var box = new Box(server.Url, Pin); box.Sdk.Initialize(Key); box.Sdk.Advance(6000);
        Check(Events(box.Sdk).Length == 0, "queue not drained before termination");
        var watch = Stopwatch.StartNew(); box.Sdk.Terminate();
        Check(watch.ElapsedMilliseconds < 200, "idle termination exceeded 200 ms: " + watch.ElapsedMilliseconds);
    }),
    ("app updates baseline legacy state, compare exact versions and suppress same-day heartbeats", () => {
        using var box = new Box(pin: Pin, appVersion: "Release A+1"); box.Sdk.Initialize(Key);
        var id = box.Sdk.InstallId;
        Check(State(box.Sdk).GetProperty("last_app_version").GetString() == "Release A+1", "first baseline");
        Check(!Events(box.Sdk).Any(e => e.GetProperty("n").GetString() == "app_updated"), "first launch update");
        box.Sdk.LegacyVersion(); box.Sdk.Dispose();
        using (var migrated = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "Release B+2")) {
            migrated.Initialize(Key); Check(migrated.InstallId == id, "legacy identity changed");
            Check(!Events(migrated).Any(e => e.GetProperty("n").GetString() == "app_updated"), "legacy initialization emitted update");
        }
        foreach (var current in new[]{"Release A+1", "Release A+1", "Release B+2", "Release A+1"}) {
            using var launch = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: current); launch.Initialize(Key);
            Check(launch.InstallId == id, "update identity changed"); launch.Initialize(Key);
        }
        using var inspect = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "Release A+1"); inspect.Initialize(Key);
        var queue = Events(inspect);
        Check(queue.Count(e => e.GetProperty("n").GetString() == "app_updated") == 3, "missed or repeated opaque transition");
        Check(queue.Count(e => e.GetProperty("n").GetString() == "heartbeat") == 1, "same-day heartbeat gate changed");
        Check(!queue.Any(e => e.GetProperty("n").GetString() == "install"), "update emitted install");
        inspect.Reset(); Check(!Events(inspect).Any(e => e.GetProperty("n").GetString() == "app_updated"), "reset retained transitions");
        Check(State(inspect).GetProperty("last_app_version").GetString() == "Release A+1", "reset lost baseline");
        inspect.Disable(); inspect.Initialize(Key); Check(!Events(inspect).Any(e => e.GetProperty("n").GetString() == "app_updated"), "disable reinit emitted update");
    }),
    ("app versions preserve Unicode scalars and reject malformed UTF-16", () => {
        foreach (var invalid in new[]{"\uD800", "\uDC00", "A\uD800B", string.Concat(Enumerable.Repeat("😀",33))})
            Check(Wire.KnownAppVersion(invalid) is null, "invalid scalar version became known");
        foreach (var valid in new[]{" Release+α ", string.Concat(Enumerable.Repeat("😀",32)), "e\u0301", "é"})
            Check(Wire.KnownAppVersion(valid) == valid, "known version was normalized or truncated");
    }),
    ("unknown app versions preserve the last known baseline", () => {
        using var box = new Box(pin: Pin, appVersion: ""); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId;
        Check(State(box.Sdk).GetProperty("last_app_version").ValueKind == JsonValueKind.Null, "missing version created baseline"); box.Sdk.Dispose();
        foreach (var current in new[]{"A", "", " \t", new string('x',33), "A", "B"}) {
            using var launch = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: current); launch.Initialize(Key); Check(launch.InstallId == id, "unknown version changed identity");
        }
        using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B"); resumed.Initialize(Key);
        Check(Events(resumed).Count(e => e.GetProperty("n").GetString() == "app_updated") == 1, "unknown versions emitted transitions");
        Check(State(resumed).GetProperty("last_app_version").GetString() == "B", "final baseline");
    }),
    ("offline retries preserve full transition metadata across later launches", () => {
        using var server = new Server { RetryAfter = "3" };
        using var box = new Box(server.Url, Pin, appVersion: "A"); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId; box.Sdk.Dispose(); server.Status = 503;
        using (var update = new Engine(box.Path, server.Url, Pin, appVersion: "B")) { update.Initialize(Key, "desktop"); _ = update.InstallId; update.Advance(3000); }
        var before = server.Requests.SelectMany(r => r.GetProperty("e").EnumerateArray()).First(e => e.GetProperty("n").GetString() == "app_updated").GetRawText();
        using var later = new Engine(box.Path, server.Url, "1788134403000", appVersion: "C"); later.Initialize(Key, "different"); Check(later.InstallId == id, "offline identity changed");
        server.Status = 202; later.Advance(10000);
        var updates = server.Requests.SelectMany(r => r.GetProperty("e").EnumerateArray()).Where(e => e.GetProperty("n").GetString() == "app_updated").ToArray();
        var oldId = JsonDocument.Parse(before).RootElement.GetProperty("id").GetString();
        Check(updates.Where(e => e.GetProperty("id").GetString() == oldId).All(e => e.GetRawText() == before), "retry rewrote transition metadata");
        Check(updates.Select(e => e.GetProperty("id").GetString()).Distinct().Count() == 2, "offline transition lost or duplicated");
        Check(updates.All(e => e.GetProperty("av").GetString() == e.GetProperty("props").GetProperty("to_version").GetString()), "version metadata mismatch");
        Check(Events(later).Length == 0, "accepted transitions retained");
    }),
    ("interrupted transition intent recovers idempotently before a later transition", () => {
        foreach (var queued in new[]{false,true}) {
            using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId; box.Sdk.Dispose();
            using (var storage = new Storage(box.Path, _ => {})) {
                Check(storage.Open(), "open recovery storage"); var queue = new EventBuffer(); var saved = storage.Load(queue);
                var props = Wire.Props("app_updated", new Dictionary<string,object?>{{"from_version","A"},{"to_version","B"}}, _ => {})!;
                var pending = EventRecord.Create("app_updated", Pin, props, metadata: new("B","macos","15","arm64","desktop","dotnet/0.1.0"));
                saved.LastAppVersion = "B"; saved.PendingUpdate = pending; Check(storage.Save(saved), "save intent");
                if (queued) { queue.Add(pending); Check(storage.Sync(queue.Snapshot(),true), "save queue before interrupted retirement"); }
            }
            using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "C"); resumed.Initialize(Key); Check(resumed.InstallId == id, "recovery rotated");
            var updates = Events(resumed).Where(e => e.GetProperty("n").GetString() == "app_updated").ToArray();
            Check(updates.Length == 2 && updates.Select(e => e.GetProperty("id").GetString()).Distinct().Count() == 2, "intent lost or duplicated");
            using var state = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(box.Path,"state.json")));
            Check(state.RootElement.GetProperty("PendingUpdate").ValueKind == JsonValueKind.Null, "intent not retired before dispatch");
        }
    }),
    ("transition eviction cannot commit a discard while its intent remains live", () => {
        using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); _ = box.Sdk.InstallId; box.Sdk.Dispose();
        using var storage = new Storage(box.Path, _ => {}); Check(storage.Open(), "open eviction storage");
        var prior = new EventBuffer(); var saved = storage.Load(prior);
        var props = Wire.Props("app_updated", new Dictionary<string,object?>{{"from_version","A"},{"to_version","B"}}, _ => {})!;
        var pending = EventRecord.Create("app_updated", Pin, props, metadata: new("B","macos","15","arm64",null,"dotnet/0.1.0"));
        saved.LastAppVersion = "B"; saved.PendingUpdate = pending; Check(storage.Save(saved), "save pending transition");
        var admitted = new EventBuffer();
        for (var i = 0; i < EventBuffer.MaxEvents; i++) admitted.Add(EventRecord.Create("x" + i, Pin, "{}"u8.ToArray()));
        admitted.Add(pending, true); Check(!admitted.ContainsId(pending.Id), "oldest transition was not evicted");
        var before = File.ReadAllBytes(storage.QueuePath); var obstacle = storage.StatePath + ".tmp"; Directory.CreateDirectory(obstacle);
        Check(!storage.CommitUpdate(saved, admitted.Snapshot()), "failed retirement succeeded");
        Check(File.ReadAllBytes(storage.QueuePath).SequenceEqual(before), "discard committed while durable intent remained live");
        Directory.Delete(obstacle);
        Check(storage.CommitUpdate(saved, admitted.Snapshot()), "commit durable discard");
        var resumed = new EventBuffer(); var recovered = storage.Load(resumed);
        Check(recovered.PendingUpdate is null && !resumed.Contains("app_updated") && recovered.LastAppVersion == "B", "discarded transition resurrected");
    }),
    ("a persisted discard intent removes an older disk copy after queue-write failure", () => {
        using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId; box.Sdk.Dispose();
        var obstacle = System.IO.Path.Combine(box.Path, "queue.jsonl.tmp");
        using (var storage = new Storage(box.Path, _ => {})) {
            Check(storage.Open(), "open discard storage"); var queue = new EventBuffer(); var saved = storage.Load(queue);
            var props = Wire.Props("app_updated", new Dictionary<string,object?>{{"from_version","A"},{"to_version","B"}}, _ => {})!;
            var pending = EventRecord.Create("app_updated", Pin, props, metadata: new("B","macos","15","arm64",null,"dotnet/0.1.0"));
            saved.LastAppVersion = "B"; saved.PendingUpdate = pending; Check(storage.Save(saved), "save recovered intent");
            queue.Add(pending); Check(storage.Sync(queue.Snapshot(), true), "persist original queue copy");
            queue.Remove([pending.Id]); Directory.CreateDirectory(obstacle);
            Check(!storage.CommitUpdate(saved, queue.Snapshot()), "blocked discard queue write succeeded");
            Check(saved.PendingUpdateDiscarded, "discard decision was not durable");
        }
        Directory.Delete(obstacle);
        using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B"); resumed.Initialize(Key); Check(resumed.InstallId == id, "discard recovery rotated identity");
        Check(!Events(resumed).Any(e => e.GetProperty("n").GetString() == "app_updated"), "older disk copy resurrected after durable discard");
    }),
    ("discarded disk records consume no recovery queue capacity", () => {
        using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); _ = box.Sdk.InstallId; box.Sdk.Dispose();
        using var storage = new Storage(box.Path, _ => {}); Check(storage.Open(), "open capped recovery storage");
        var prior = new EventBuffer(); var saved = storage.Load(prior);
        var props = Wire.Props("app_updated", new Dictionary<string,object?>{{"from_version","A"},{"to_version","B"}}, _ => {})!;
        var discarded = EventRecord.Create("app_updated", Pin, props, metadata: new("B","macos","15","arm64",null,"dotnet/0.1.0"));
        saved.LastAppVersion = "B"; saved.PendingUpdate = discarded; saved.PendingUpdateDiscarded = true; Check(storage.Save(saved), "save discard marker");
        var oldDisk = new EventBuffer();
        for (var i = 0; i < EventBuffer.MaxEvents - 1; i++) oldDisk.Add(EventRecord.Create("kept" + i, Pin, "{}"u8.ToArray()));
        oldDisk.Add(discarded); Check(storage.Sync(oldDisk.Snapshot(), true), "seed older disk copy");
        var admitted = new EventBuffer(); admitted.Add(EventRecord.Create("startup", Pin, "{}"u8.ToArray()));
        _ = storage.Load(admitted);
        Check(admitted.Size.Count == EventBuffer.MaxEvents && admitted.Contains("kept0") && admitted.Contains("startup") && !admitted.ContainsId(discarded.Id), "discarded disk record evicted a retained event");
    }),
    ("startup admissions cannot resurrect a pending update evicted during journal replay", () => {
        using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId; box.Sdk.Dispose();
        using (var storage = new Storage(box.Path, _ => {})) {
            Check(storage.Open(), "open interrupted retirement storage");
            var saved = storage.Load(new EventBuffer());
            var props = Wire.Props("app_updated", new Dictionary<string,object?>{{"from_version","A"},{"to_version","B"}}, _ => {})!;
            var update = EventRecord.Create("app_updated", Pin, props, metadata: new("B","macos","15","arm64",null,"dotnet/0.1.0"));
            saved.LastAppVersion = "B"; saved.PendingUpdate = update; Check(storage.Save(saved), "persist pending intent");
            var queue = new EventBuffer(); queue.Add(update);
            for (var i = 0; i < EventBuffer.MaxEvents - 1; i++) queue.Add(EventRecord.Create("kept" + i, Pin, "{}"u8.ToArray()));
            Check(storage.Sync(queue.Snapshot(), true), "persist queue before interrupted retirement");
        }
        using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B");
        lock (AdmissionGate(resumed)) { resumed.Initialize(Key); resumed.Track("startup"); }
        Check(resumed.InstallId == id, "recovery changed identity");
        var events = Events(resumed);
        Check(events.Length == EventBuffer.MaxEvents && !events.Any(e => e.GetProperty("n").GetString() == "app_updated"), "cap-evicted update was restored from its intent");
        Check(events.Any(e => e.GetProperty("n").GetString() == "kept0") && events.Any(e => e.GetProperty("n").GetString() == "startup"), "recovery evicted a newer retained event");
        using var durable = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(box.Path, "state.json")));
        Check(durable.RootElement.GetProperty("PendingUpdate").ValueKind == JsonValueKind.Null, "discarded intent was not retired");
    }),
    ("reset cannot reassign an old transition after an interrupted checkpoint", () => {
        foreach (var blocked in new[]{"state.json.tmp", "queue.jsonl.tmp"}) {
            using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); var oldId = box.Sdk.InstallId; box.Sdk.Dispose();
            var obstacle = System.IO.Path.Combine(box.Path, blocked);
            using (var update = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B")) {
                update.Initialize(Key); Check(update.InstallId == oldId, "transition changed identity");
                Check(Events(update).Any(e => e.GetProperty("n").GetString() == "app_updated"), "missing reset input transition");
                Directory.CreateDirectory(obstacle); update.Reset(); Check(update.InstallId == "", "failed reset remained active");
            }
            Directory.Delete(obstacle);
            using var resumed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B"); resumed.Initialize(Key);
            Check(resumed.InstallId == oldId, "new identity committed before the old queue was cleared");
            if (blocked == "state.json.tmp") Check(!Events(resumed).Any(e => e.GetProperty("n").GetString() == "app_updated"), "reset clear was undone after state-write interruption");
        }
    }),
    ("failed transition writes preserve a recoverable baseline and never dispatch", () => {
        foreach (var blocked in new[]{"state.json.tmp","queue.jsonl.tmp"}) {
            using var box = new Box(pin: Pin, appVersion: "A"); box.Sdk.Initialize(Key); var id = box.Sdk.InstallId; box.Sdk.Dispose();
            var obstacle = System.IO.Path.Combine(box.Path,blocked); Directory.CreateDirectory(obstacle);
            using (var failed = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B")) { failed.Initialize(Key); Check(failed.InstallId == "", "failed transition remained dispatchable"); }
            Directory.Delete(obstacle);
            using var recovered = new Engine(box.Path, "http://127.0.0.1:1/v1/e", Pin, appVersion: "B"); recovered.Initialize(Key); Check(recovered.InstallId == id, "write failure changed identity");
            Check(Events(recovered).Count(e => e.GetProperty("n").GetString() == "app_updated") == 1, "write failure lost or duplicated transition");
            Check(State(recovered).GetProperty("last_app_version").GetString() == "B", "write failure baseline mismatch");
        }
    }),
    ("C11: 10,000 calls over 10 seconds, accounted memory and p99", () => {
        using var box=new Box(pin:Pin);box.Sdk.Initialize(Key);_=box.Sdk.InstallId;
        for(var i=0;i<100;i++)box.Sdk.Track("warmup");
        var times=new double[10000];var pace=Stopwatch.StartNew();
        for(var i=0;i<times.Length;i++) {
            var start=Stopwatch.GetTimestamp();box.Sdk.Track("budget");times[i]=Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if(i%10==9) { var wait=(i+1)-pace.ElapsedMilliseconds;if(wait>0)Thread.Sleep((int)wait); }
        }
        Array.Sort(times); var peak=State(box.Sdk).GetProperty("peak_accounted_state_bytes").GetInt32();
        Console.WriteLine($"C11: p99={times[9899]:F1} us, peak accounted={peak} B, duration={pace.ElapsedMilliseconds} ms");
        Check(times[9899]<=1000,"track p99 >1ms");Check(peak<=2097152,"SDK memory >2MiB");
    })
};
foreach(var test in tests) {
    try { test.Run(); Console.WriteLine("PASS "+test.Name); }
    catch(Exception error) { failures++; Console.Error.WriteLine("FAIL "+test.Name+": "+error); }
}
Console.WriteLine($"{tests.Length-failures} passed, {failures} failed; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}");
return failures==0?0:1;

static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
static object AdmissionGate(Engine sdk) => typeof(Engine).GetField("gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sdk)!;
static JsonElement State(Engine sdk) { using var doc=JsonDocument.Parse(sdk.Export());return doc.RootElement.Clone(); }
static JsonElement[] Events(Engine sdk)=>State(sdk).GetProperty("queue").GetProperty("events").EnumerateArray().ToArray();
static void Wait(Func<bool> predicate,int millis) { var until=Environment.TickCount64+millis;while(!predicate()){if(Environment.TickCount64>=until)throw new TimeoutException();Thread.Sleep(10);} }

internal static class Uuid { internal static int Version(this Guid id)=>Convert.ToInt32(id.ToString("D")[14].ToString(),16); }
internal sealed class Box : IDisposable
{
    internal string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"jelto-dotnet-test-"+Guid.NewGuid());
    internal Engine Sdk {get;}
    internal Box(string? endpoint=null,string? pin=null,string? platform=null,string? appVersion=null)=>Sdk=new(Path,endpoint??"http://127.0.0.1:1/v1/e",pin,platform,appVersion);
    public void Dispose(){Sdk.Dispose();try{Directory.Delete(Path,true);}catch{}}
}
internal sealed class Server : IDisposable
{
    private readonly HttpListener listener=new();
    private readonly CancellationTokenSource cancelled=new();
    internal string Url {get;}
    internal ConcurrentQueue<string> Bodies {get;}=new();
    internal IEnumerable<JsonElement> Requests=>Bodies.Select(b=>{using var doc=JsonDocument.Parse(b);return doc.RootElement.Clone();});
    internal ConcurrentQueue<string?> Cookies {get;}=new();
    internal int Status=202,DelayMs;
    internal string Body="{}";
    internal string? RetryAfter,Redirect;
    internal Server(){
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        var prefix=$"http://127.0.0.1:{port}/";Url=prefix+"v1/e";listener.Prefixes.Add(prefix);listener.Start();_=Serve();
    }
    private async Task Serve(){
        while(!cancelled.IsCancellationRequested){
            HttpListenerContext ctx;try{ctx=await listener.GetContextAsync();}catch{return;}
            _=Reply(ctx);
        }
    }
    private async Task Reply(HttpListenerContext ctx){
        try{
            using var input=new StreamReader(ctx.Request.InputStream);Bodies.Enqueue(await input.ReadToEndAsync());Cookies.Enqueue(ctx.Request.Headers["Cookie"]);
            var status=Status;var body=Body;var delay=DelayMs;
            if(delay>0)await Task.Delay(delay,cancelled.Token);
            ctx.Response.StatusCode=status;ctx.Response.Headers["Set-Cookie"]="tracking=no; Path=/";
            if(RetryAfter is not null)ctx.Response.Headers["Retry-After"]=RetryAfter;
            if(Redirect is not null)ctx.Response.Headers["Location"]=Redirect;
            var bytes=Encoding.UTF8.GetBytes(body);ctx.Response.ContentLength64=bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);ctx.Response.Close();
        }catch{}
    }
    public void Dispose(){cancelled.Cancel();listener.Close();cancelled.Dispose();}
}
