using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.ExternalServices;

namespace NovellMaker.Runtime.ExternalServices;

/// <summary>Only public GitHub release metadata. Owns its client; never follows redirects or sends credentials.</summary>
public sealed class GitHubReleaseProvider : IReleaseCheckProvider, IDisposable
{
    public const int MaximumBodyBytes = 256 * 1024;
    public const int MaximumHeaderBytes = 16 * 1024;
    private readonly HttpClient client;
    private readonly TimeProvider clock;

    public GitHubReleaseProvider() : this(CreateHandler(), TimeProvider.System) { }

    internal GitHubReleaseProvider(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        this.clock = clock ?? TimeProvider.System;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, PreAuthenticate = false,
        DefaultProxyCredentials = null, MaxResponseHeadersLength = MaximumHeaderBytes / 1024,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        MaxConnectionsPerServer = 1, ConnectTimeout = ReleaseCheckService.Deadline
    };

    public async Task<ReleaseProviderReply> CheckAsync(ReleaseCheckConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        // Once validated, rate-limit metadata applies even when status, body reading or release parsing fails.
        int? delay = null;
        ReleaseProviderReply Reply(ReleaseCheckCode code, ReleaseCheckData? release = null) => new(code, release, delay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{configuration.Owner}/{configuration.Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("NovellMaker-ReleaseCheck/1");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            CheckHeaders(response);
            delay = RetryAfter(response);
            var status = (int)response.StatusCode;
            if (status == 429 || status == 403 && (response.Headers.Contains("Retry-After") || RemainingZero(response)))
                return new(ReleaseCheckCode.RateLimited, RetryAfterSeconds: delay ?? 60);
            if (status == 404) return Reply(ReleaseCheckCode.NotFound);
            if (status is 401 or 403) return Reply(ReleaseCheckCode.AccessDenied);
            if (status >= 500 && status <= 599) return Reply(ReleaseCheckCode.ProviderUnavailable);
            if (status != 200) return Reply(ReleaseCheckCode.InvalidResponse);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not ("application/json" or "application/vnd.github+json")) return Reply(ReleaseCheckCode.InvalidResponse);
            if (response.Content.Headers.ContentLength > MaximumBodyBytes) return Reply(ReleaseCheckCode.ResponseTooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBodyBytes + 1 - (int)body.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > MaximumBodyBytes) return Reply(ReleaseCheckCode.ResponseTooLarge);
                body.Write(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Reject invalid UTF-8 even in unused JSON strings, rather than silently replacing bytes.
            var text = new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateKeys(document.RootElement, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("html_url", out var page) || page.ValueKind != JsonValueKind.String)
                return Reply(ReleaseCheckCode.InvalidResponse);
            var rawTag = tag.GetString();
            if (string.IsNullOrEmpty(rawTag)) return Reply(ReleaseCheckCode.InvalidResponse);
            if (!ReleaseVersion.TryParse(rawTag, out var version, tag: true)) return Reply(ReleaseCheckCode.InvalidVersion);
            var url = page.GetString();
            if (!configuration.IsValidPage(url, version) ||
                Uri.UnescapeDataString(url![(url!.LastIndexOf('/') + 1)..]) != rawTag)
                return Reply(ReleaseCheckCode.InvalidResponse);
            var data = new ReleaseCheckData(configuration.CurrentVersion.ToString(), version.ToString(),
                version.CompareTo(configuration.CurrentVersion) > 0, url!, clock.GetUtcNow().ToUniversalTime());
            cancellationToken.ThrowIfCancellationRequested();
            return data.IsValid(configuration) ? Reply(ReleaseCheckCode.Ok, data) : Reply(ReleaseCheckCode.ResponseTooLarge);
        }
        catch (HeaderLimitException) { return Reply(ReleaseCheckCode.ResponseTooLarge); }
        catch (HttpRequestException error)
        {
            return Reply(error.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => ReleaseCheckCode.TlsFailure,
                HttpRequestError.ConfigurationLimitExceeded => ReleaseCheckCode.ResponseTooLarge,
                HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError => ReleaseCheckCode.InvalidResponse,
                _ => ReleaseCheckCode.NetworkUnavailable
            });
        }
        catch (IOException) { return Reply(ReleaseCheckCode.NetworkUnavailable); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException)
        { return Reply(ReleaseCheckCode.InvalidResponse); }
    }

    private sealed class HeaderLimitException : Exception;

    private static void CheckHeaders(HttpResponseMessage response)
    {
        long size = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            foreach (var value in header.Value)
            {
                size += Encoding.UTF8.GetByteCount(header.Key) + Encoding.UTF8.GetByteCount(value) + 4;
                if (size > MaximumHeaderBytes) throw new HeaderLimitException();
            }
    }

    private static void ValidateKeys(JsonElement root, CancellationToken token)
    {
        var remaining = new Stack<JsonElement>(); remaining.Push(root);
        while (remaining.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new JsonException();
                    remaining.Push(property.Value);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var child in item.EnumerateArray()) remaining.Push(child);
        }
    }

    private static bool RemainingZero(HttpResponseMessage response) => response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        values.SingleOrDefault() == "0";

    private int? RetryAfter(HttpResponseMessage response)
    {
        var delay = 0;
        static int Seconds(double value) => (int)Math.Clamp(Math.Ceiling(value), 1, int.MaxValue);
        if (response.Headers.TryGetValues("Retry-After", out var retries))
        {
            var text = retries.SingleOrDefault();
            if (text is not null)
            {
                if (text.Length > 0 && text.All(char.IsAsciiDigit))
                    delay = text.Length <= 128 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? Math.Max(1, seconds) : int.MaxValue;
                else if (text.Length <= 128 && DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    delay = Seconds((date - clock.GetUtcNow()).TotalSeconds);
            }
        }
        if (RemainingZero(response) && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets))
        {
            var text = resets.SingleOrDefault();
            if (text is { Length: > 0 } && text.All(char.IsAsciiDigit))
            {
                var resetDelay = text.Length <= 128 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
                    ? Seconds((double)timestamp - clock.GetUtcNow().ToUnixTimeSeconds()) : int.MaxValue;
                delay = Math.Max(delay, resetDelay);
            }
        }
        return delay == 0 ? null : delay;
    }

    public void Dispose() => client.Dispose();
}
