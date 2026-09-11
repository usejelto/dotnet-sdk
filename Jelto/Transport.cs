using System.Net;

namespace Jelto;

internal sealed record Outcome(int Status, byte[] Body, string? RetryAfter, bool NetworkError = false)
{
    internal bool Retryable => NetworkError || Status is 429 or 503;
}

internal sealed class Transport : IDisposable
{
    private HttpClient? client;
    internal async Task<Outcome> Post(Uri endpoint, byte[] body, string? mock, CancellationToken cancelled)
    {
        var status = 0;
        string? retryAfter = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelled);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            client ??= new HttpClient(new SocketsHttpHandler {
                AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 1, MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
            if (mock is not null) request.Headers.TryAddWithoutValidation("X-Mock", mock);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (response.Headers.TryGetValues("Retry-After", out var headers)) retryAfter = headers.FirstOrDefault();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (output.Length <= Wire.MaxBody)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Wire.MaxBody + 1 - output.Length)), timeout.Token).ConfigureAwait(false);
                if (read == 0) return new(status, output.ToArray(), retryAfter);
                output.Write(buffer, 0, read);
            }
            return new(status, [], retryAfter);
        }
        // Once a status exists, malformed/oversized/interrupted bodies cannot undo acceptance.
        catch { return new(status, [], retryAfter, status == 0); }
    }
    public void Dispose() { client?.Dispose(); client = null; }
}
