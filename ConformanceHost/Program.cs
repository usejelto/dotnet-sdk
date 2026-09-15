using System.Diagnostics;
using System.Text.Json;
using Jelto;

var rawNow = Environment.GetEnvironmentVariable("JELTO_NOW");
if ((rawNow is not null && Wire.Instant(rawNow.Trim()) is null)
    || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JELTO_ENDPOINT"))
    || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JELTO_STATE_DIR")))
{
    Console.Error.WriteLine("conformance-host: require JELTO_ENDPOINT, JELTO_STATE_DIR and valid whole-millisecond JELTO_NOW");
    return 2;
}
var sdk = JeltoClient.Conformance;
string? line;
while ((line = Console.ReadLine()) is not null)
{
    var first = line.IndexOf(' ');
    var cmd = first < 0 ? line : line[..first];
    var argument = first < 0 ? "" : line[(first + 1)..];
    var reply = new Dictionary<string, object?> { ["cmd"] = cmd, ["ok"] = true };
    try
    {
        switch (cmd)
        {
            case "init":
                var initArgs = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (initArgs.Length is < 1 or > 2) throw new ArgumentException("init <key> [app]");
                var watch = Stopwatch.StartNew();
                var origin = Environment.GetEnvironmentVariable("JELTO_INSTALL_ORIGIN") switch { "new" => InstallOrigin.New, "existing" => InstallOrigin.Existing, _ => InstallOrigin.Unknown };
                JeltoClient.Initialize(initArgs[0], initArgs.Length > 1 ? initArgs[1] : null, installOrigin: origin);
                reply["us"] = (long)watch.Elapsed.TotalMicroseconds;
                break;
            case "track":
                var (name, rest) = Token(argument);
                if (name == "") throw new ArgumentException("track <name> [json-props]");
                Dictionary<string, object?>? props = null;
                if (rest != "")
                {
                    using var doc = JsonDocument.Parse(rest);
                    props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                }
                JeltoClient.Track(name, props); break;
            case "setprops":
                if (argument == "") throw new ArgumentException("setprops <json>");
                JeltoClient.SetProps(JsonSerializer.Deserialize<Dictionary<string, string>>(argument) ?? throw new ArgumentException("setprops <json>")); break;
            case "onboarding":
                var (step, tail) = Token(argument);
                var (status, reasonText) = Token(tail);
                if (status == "") throw new ArgumentException("onboarding <step> <status> [reason]");
                var (reason, _) = Token(reasonText);
                JeltoClient.Onboarding(step, status, reason == "" ? null : reason); break;
            case "installid": reply["value"] = JeltoClient.InstallId; break;
            case "dumpstate": using (var doc = JsonDocument.Parse(sdk.Export())) reply["state"] = doc.RootElement.Clone(); break;
            case "reset": JeltoClient.Reset(); break;
            case "legacyversion": sdk.LegacyVersion(); break;
            case "disable": JeltoClient.Disable(); break;
            case "sleep": sdk.Advance(long.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "exit": sdk.Terminate(); break;
            default: throw new ArgumentException("unknown command");
        }
    }
    catch (Exception error) { reply["ok"] = false; reply["error"] = error.Message; }
    Console.WriteLine(JsonSerializer.Serialize(reply));
    if (cmd == "exit") return 0;
}
sdk.Terminate();
return 0;

static (string Value, string Remaining) Token(string input)
{
    input = input.TrimStart();
    if (input == "") return ("", "");
    if (input[0] == '"')
    {
        var escaped = false;
        for (var i = 1; i < input.Length; i++)
        {
            if (!escaped && input[i] == '"') return (JsonSerializer.Deserialize<string>(input[..(i + 1)])!, input[(i + 1)..].TrimStart());
            if (!escaped && input[i] == '\\') escaped = true; else escaped = false;
        }
        throw new ArgumentException("unterminated quoted token");
    }
    var end = input.IndexOf(' ');
    return end < 0 ? (input, "") : (input[..end], input[(end + 1)..].TrimStart());
}
