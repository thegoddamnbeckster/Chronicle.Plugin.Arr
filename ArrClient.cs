using System.Net;
using System.Text.Json;
using Chronicle.Plugins;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.Arr;

/// <summary>
/// The only code in this plugin that talks to the network, and it can only ever GET: there is no method that sends a body, so
/// the plugin cannot add, change or delete anything in Radarr, Sonarr, Lidarr or Readarr.
///
/// The address is one the owner typed, usually on their own network, so private addresses are allowed here (unlike an address a
/// metadata provider supplied). To keep that safe the client follows no redirects, accepts only http and https, sends the key in
/// a header and never writes any part of it to a log or an error, and refuses a response larger than <see cref="MaxResponseBytes"/>.
///
/// Failure contract: a rejected key throws <see cref="PluginAuthException"/> (once; the caller latches it); a rate limit (429) is
/// retried a bounded number of times honouring Retry-After, then thrown as an <see cref="HttpRequestException"/> carrying
/// <see cref="HttpStatusCode.TooManyRequests"/> so the host pauses; anything else returns null and is logged.
/// </summary>
internal sealed class ArrClient : IDisposable
{
    /// <summary>A library of tens of thousands of films is a few tens of megabytes of JSON; far past this is not an arr app.</summary>
    internal const long MaxResponseBytes = 150L * 1024 * 1024;

    /// <summary>The host gives one call 25 seconds; a read of a large library stays well inside that.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How many times a 429 is retried, and the longest any one wait may be (a Retry-After above this is capped).</summary>
    internal const int MaxRetries = 2;
    internal static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(4);

    internal const string PluginId = "chronicle.plugin.arr";

    private static readonly ILogger Log = Serilog.Log.ForContext<ArrClient>();

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _ownsHttp;

    /// <summary>The User-Agent every request sends: names Chronicle and this plugin's version, nothing about the owner.</summary>
    public static string UserAgent(string version) => $"Chronicle-Arr/{version}";

    public ArrClient(string version, HttpClient? http = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = RequestTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent(version));
    }

    /// <summary>Accepts only an http or https address, and returns it with a trailing slash so paths append cleanly.</summary>
    public static Uri? ParseBaseUri(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed.EndsWith('/') ? trimmed : trimmed + "/", UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        // An address with a user name or password in it would put credentials in URLs and logs.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri;
    }

    /// <summary>GETs <paramref name="path"/> (starting with "/api/...") and returns the parsed JSON, or null when the application
    /// could not be read. Throws <see cref="PluginAuthException"/> for a rejected key and <see cref="HttpRequestException"/>
    /// (TooManyRequests) when rate limited past the retries.</summary>
    public async Task<JsonDocument?> GetJsonAsync(ArrEndpoint endpoint, string path, CancellationToken ct)
    {
        var app = ArrApps.Label(endpoint.App);
        var uri = new Uri(endpoint.BaseUri, path.TrimStart('/'));

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("X-Api-Key", endpoint.ApiKey);
            request.Headers.Accept.ParseAdd("application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                // The message names the host, not the key; the key is only ever in a header.
                Log.Warning("{PluginId}: {App} could not be reached at {Path}: {Message}", PluginId, app, path, ex.Message);
                return null;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new PluginAuthException(PluginId,
                        $"{app} rejected the API key. Copy the key again from {app}'s Settings, General page into this plugin's {app} API key box, then save.");

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= MaxRetries)
                        throw new HttpRequestException($"{app} is rate limiting requests (HTTP 429).", null, HttpStatusCode.TooManyRequests);
                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(1 << attempt);
                    await _delay(wait > MaxRetryWait ? MaxRetryWait : wait, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("{PluginId}: {App} answered {Path} with HTTP {Status}", PluginId, app, path, (int)response.StatusCode);
                    return null;
                }
                return await ReadBodyAsync(response, app, path, ct);
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var h = response.Headers.RetryAfter;
        if (h?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (h?.Date is { } date)
        {
            var span = date - DateTimeOffset.UtcNow;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }
        return null;
    }

    private static async Task<JsonDocument?> ReadBodyAsync(HttpResponseMessage response, string app, string path, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            Log.Warning("{PluginId}: {App} answered {Path} with {Bytes} bytes; refusing", PluginId, app, path, response.Content.Headers.ContentLength);
            return null;
        }
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    Log.Warning("{PluginId}: {App} answered {Path} with more than the {Max} byte limit; refusing", PluginId, app, path, MaxResponseBytes);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            Log.Warning("{PluginId}: {App} sent something that is not readable JSON at {Path}: {Message}", PluginId, app, path, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
