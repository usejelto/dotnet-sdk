using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jelto;

internal static partial class Wire
{
    internal const int MaxBody = 65536;
    [GeneratedRegex(@"\Aprd_[a-z0-9]{10}\z")] internal static partial Regex Product();
    [GeneratedRegex(@"\A[a-z0-9-]{1,32}\z")] internal static partial Regex Slug();
    [GeneratedRegex(@"\A[a-z0-9_:.-]{1,64}\z")] internal static partial Regex Name();
    [GeneratedRegex(@"\A[a-z0-9_]{1,32}\z")] internal static partial Regex PropKey();
    [GeneratedRegex(@"\A[a-z0-9_.-]{1,24}\z")] internal static partial Regex InstallValue();
    [GeneratedRegex(@"\A[a-z0-9_-]{1,32}\z")] internal static partial Regex Step();
    [GeneratedRegex(@"\A[a-z0-9_.-]{1,64}\z")] internal static partial Regex Reason();
    [GeneratedRegex(@"\A[a-z]+/[0-9A-Za-z.+-]{1,24}\z")] internal static partial Regex Version();
    [GeneratedRegex(@"\A-?[0-9]+\z")] internal static partial Regex Integer();
    [GeneratedRegex(@"\A[0-9]+\z")] internal static partial Regex Seconds();

    internal static string Decimal(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string? KnownAppVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var remaining = value.AsSpan(); var count = 0;
        while (!remaining.IsEmpty)
        {
            // Enumeration replaces malformed UTF-16. Reject it instead, so a
            // baseline cannot change when JSON serializes the same observation.
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done || ++count > 32) return null;
            remaining = remaining[consumed..];
        }
        return value;
    }
    internal static BigInteger? Instant(string? value) => value is not null && Integer().IsMatch(value)
        && BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return stream.ToArray();
    }

    // JsonElement is the host's raw-number seam; it goes through this same type/grammar gate.
    internal static byte[]? Props(string name, IReadOnlyDictionary<string, object?>? props, Action<string> log)
    {
        if (!Name().IsMatch(name)) { log("drop event: spec/wire-v1.md §3 `n` is ^[a-z0-9_:.-]{1,64}$"); return null; }
        if (props?.Count > 20) { log("drop props: spec/wire-v1.md §3 caps them at 20"); return null; }
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (props is not null)
        {
            foreach (var (key, value) in props)
            {
                if (values.Count == 20 || !PropKey().IsMatch(key)) { log("drop props: keys are ^[a-z0-9_]{1,32}$; maximum 20"); return null; }
                JsonElement item;
                switch (value)
                {
                    case string s:
                        if (s.EnumerateRunes().Take(201).Count() > 200) { log("drop props: spec/wire-v1.md §3 caps a string at 200"); return null; }
                        item = JsonSerializer.SerializeToElement(s); break;
                    case bool b: item = JsonSerializer.SerializeToElement(b); break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                        item = JsonSerializer.SerializeToElement(value, value.GetType()); break;
                    case float f when float.IsFinite(f): item = JsonSerializer.SerializeToElement(f); break;
                    case double d when double.IsFinite(d): item = JsonSerializer.SerializeToElement(d); break;
                    case BigInteger big:
                        if (big.GetByteCount() > 16000) { log("drop props: number exceeds event budget"); return null; }
                        using (var doc = JsonDocument.Parse(Decimal(big))) item = doc.RootElement.Clone();
                        break;
                    case JsonElement raw: item = raw; break;
                    default: log("drop props: spec/wire-v1.md §3 allows a string, a number or a boolean"); return null;
                }
                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                { log("drop props: spec/wire-v1.md §3 allows a string, a number or a boolean"); return null; }
                if (item.ValueKind == JsonValueKind.String && item.GetString()!.EnumerateRunes().Take(201).Count() > 200)
                { log("drop props: spec/wire-v1.md §3 caps a string at 200"); return null; }
                if (item.GetRawText().Length > MaxBody / 2) { log("drop props: value exceeds event budget"); return null; }
                values.Add(key, item);
            }
        }
        if (name.StartsWith("onboarding:", StringComparison.Ordinal))
        {
            if (!Step().IsMatch(name[11..])) { log("drop onboarding: `<step>` is ^[a-z0-9_-]{1,32}$"); return null; }
            if (!values.TryGetValue("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() is not ("ok" or "fail" or "skip"))
            { log("drop onboarding: status is ok|fail|skip"); return null; }
            if (values.TryGetValue("reason", out var reason) && (reason.ValueKind != JsonValueKind.String || !Reason().IsMatch(reason.GetString()!)))
            { log("drop onboarding: reason is ^[a-z0-9_.-]+$ and <= 64 chars"); return null; }
        }
        if (name == "heartbeat" && values.Any(p => p.Value.ValueKind != JsonValueKind.String || !InstallValue().IsMatch(p.Value.GetString()!)))
        { log("drop heartbeat: install values are ^[a-z0-9_.-]{1,24}$"); return null; }
        if (name == "app_updated" && (values.Count != 2 || !values.TryGetValue("from_version", out var from) || !values.TryGetValue("to_version", out var to)
            || from.ValueKind != JsonValueKind.String || to.ValueKind != JsonValueKind.String || KnownAppVersion(from.GetString()) is null || KnownAppVersion(to.GetString()) is null || from.GetString() == to.GetString()))
        { log("drop app_updated: requires different known from_version and to_version"); return null; }
        if (name is "purchase" or "pageview" or "engagement" or "click:download" or "click:outbound")
        { log("drop event: reserved for another surface or server"); return null; }
        var encoded = Json(w => { w.WriteStartObject(); foreach (var p in values) { w.WritePropertyName(p.Key); p.Value.WriteTo(w); } w.WriteEndObject(); });
        if (encoded.Length > MaxBody - 4096) { log("drop props: event exceeds batch budget"); return null; }
        return encoded;
    }

    internal static Dictionary<string, string> InstallProps(IReadOnlyDictionary<string, string> props, Action<string> log)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Bound work even for an adversarial IReadOnlyDictionary implementation.
        foreach (var (key, value) in props.Take(21))
        {
            if (!PropKey().IsMatch(key)) { log("drop install property: keys are ^[a-z0-9_]{1,32}$"); continue; }
            if (value is null || !InstallValue().IsMatch(value)) { log("drop install property: values are ^[a-z0-9_.-]{1,24}$"); continue; }
            result[key] = value;
        }
        return result;
    }
}
