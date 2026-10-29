using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Runtime.ExternalServices;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ReleaseCheckProviderTests
{
    private static readonly ReleaseCheckConfiguration Config = new("writer", "novel", "1.9.0");
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_000_000) + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static HttpResponseMessage Response(int status = 200, string? body = null) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body ?? Body(), Encoding.UTF8, "application/json")
    };
    private static string Body(string tag = "v1.10.0", string? url = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease = false,
        html_url = url ?? "https://github.com/writer/novel/releases/tag/" + tag,
        unused = new { text = "Ignored", items = new[] { 1, 2, 3 } }
    });
    private static async Task<ReleaseProviderReply> Read(HttpResponseMessage response)
    {
        using var provider = new GitHubReleaseProvider(new Handler((_, _) => Task.FromResult(response)), new Clock());
        return await provider.CheckAsync(Config, CancellationToken.None);
    }

    [TestMethod]
    public async Task OnlyFixedEndpointAndHeadersAreSentAndResourcesAreOwned()
    {
        var handler = new Handler((request, token) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.github.com/repos/writer/novel/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
            Assert.AreEqual("NovellMaker-ReleaseCheck/1", request.Headers.UserAgent.ToString());
            Assert.AreEqual("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.IsNull(request.Headers.Authorization); Assert.IsNull(request.Content);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.AreEqual(3, request.Headers.Count());
            return Task.FromResult(Response());
        });
        using (var provider = new GitHubReleaseProvider(handler, new Clock()))
        {
            var result = await provider.CheckAsync(Config, CancellationToken.None);
            Assert.AreEqual(ReleaseCheckCode.Ok, result.Code); Assert.AreEqual("1.10.0.0", result.Release!.LatestVersion);
            Assert.AreEqual("1.9.0.0", result.Release.CurrentVersion); Assert.IsTrue(result.Release.UpdateAvailable);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_000_000), result.Release.CheckedAt);
            Assert.IsFalse(result.Release.FromCache); Assert.AreEqual(1, handler.Calls);
        }
        Assert.IsTrue(handler.Disposed);
        using var sockets = GitHubReleaseProvider.CreateHandler();
        Assert.IsFalse(sockets.AllowAutoRedirect); Assert.IsFalse(sockets.UseCookies); Assert.IsFalse(sockets.PreAuthenticate);
        Assert.IsNull(sockets.Credentials); Assert.IsNull(sockets.DefaultProxyCredentials);
        Assert.AreEqual(16, sockets.MaxResponseHeadersLength); Assert.AreEqual(1, sockets.MaxConnectionsPerServer);
        Assert.AreEqual(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, sockets.AutomaticDecompression);
        Assert.AreEqual(TimeSpan.FromSeconds(10), sockets.ConnectTimeout);
    }

    [TestMethod]
    [DataRow("v1.9.0", false)] [DataRow("1.8.99.65535", false)] [DataRow("1.10.0.0", true)]
    [DataRow("v65535.65535.65535.65535", true)]
    public async Task NumericVersionsAreCompared(string tag, bool update)
    {
        var result = await Read(Response(body: Body(tag)));
        Assert.AreEqual(ReleaseCheckCode.Ok, result.Code); Assert.AreEqual(update, result.Release!.UpdateAvailable);
    }

    [TestMethod]
    [DataRow(301, ReleaseCheckCode.InvalidResponse)] [DataRow(302, ReleaseCheckCode.InvalidResponse)]
    [DataRow(304, ReleaseCheckCode.InvalidResponse)] [DataRow(400, ReleaseCheckCode.InvalidResponse)]
    [DataRow(401, ReleaseCheckCode.AccessDenied)] [DataRow(403, ReleaseCheckCode.AccessDenied)]
    [DataRow(404, ReleaseCheckCode.NotFound)] [DataRow(410, ReleaseCheckCode.InvalidResponse)]
    [DataRow(429, ReleaseCheckCode.RateLimited)] [DataRow(500, ReleaseCheckCode.ProviderUnavailable)]
    [DataRow(503, ReleaseCheckCode.ProviderUnavailable)]
    public async Task HttpFailuresAreMappedWithoutPayloadOrRetries(int status, ReleaseCheckCode code)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(status, "secret-token")));
        using var provider = new GitHubReleaseProvider(handler);
        var result = await provider.CheckAsync(Config, CancellationToken.None);
        Assert.AreEqual(code, result.Code); Assert.IsNull(result.Release); Assert.AreEqual(1, handler.Calls);
        if (status == 429) Assert.AreEqual(60, result.RetryAfterSeconds);
    }

    [TestMethod]
    [DataRow("{}")] [DataRow("[]")] [DataRow("null")] [DataRow("not json")]
    [DataRow("{\"tag_name\": 4,\"draft\":false,\"prerelease\":false,\"html_url\":\"x\"}")]
    [DataRow("{\"tag_name\":\"\",\"draft\":false,\"prerelease\":false,\"html_url\":\"x\"}")]
    [DataRow("{\"tag_name\":\"v1.10.0\",\"draft\":true,\"prerelease\":false,\"html_url\":\"x\"}")]
    [DataRow("{\"tag_name\":\"v1.10.0\",\"draft\":false,\"prerelease\":true,\"html_url\":\"x\"}")]
    [DataRow("{\"tag_name\":\"v1.10.0\",\"draft\":false,\"prerelease\":false,\"html_url\":5}")]
    [DataRow("{\"tag_name\":\"v1.10.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://evil.test\"}")]
    public async Task InvalidJsonAndFieldsRejectWholeResponse(string body) =>
        Assert.AreEqual(ReleaseCheckCode.InvalidResponse, (await Read(Response(body: body))).Code);

    [TestMethod]
    [DataRow("v1.0.0-beta")] [DataRow("V1.0.0")] [DataRow("release-1.0.0")] [DataRow("1.00.0")] [DataRow("65536.0.0")]
    public async Task UnsupportedTagIsNotGuessed(string tag) =>
        Assert.AreEqual(ReleaseCheckCode.InvalidVersion, (await Read(Response(body: Body(tag)))).Code);

    [TestMethod]
    public async Task DuplicateEscapedKeysNestedDuplicatesAndExcessiveDepthAreRejected()
    {
        var body = Body();
        string[] bad = [body.Replace("\"draft\":false", "\"draft\":false,\"dr\\u0061ft\":false", StringComparison.Ordinal),
            body.Replace("\"text\":\"Ignored\"", "\"x\":1,\"x\":2", StringComparison.Ordinal),
            body.Replace("\"text\":\"Ignored\"", "\"x\":" + new string('[', 17) + "0" + new string(']', 17), StringComparison.Ordinal)];
        foreach (var input in bad) Assert.AreEqual(ReleaseCheckCode.InvalidResponse, (await Read(Response(body: input))).Code);
        Assert.AreEqual(ReleaseCheckCode.InvalidResponse,
            (await Read(Response(body: Body("v1.10.0", "https://github.com/writer/novel/releases/tag/1.10.0.0")))).Code);
    }

    [TestMethod]
    public async Task MediaTypeAndUtf8AreValidated()
    {
        using var wrongType = Response(); wrongType.Content.Headers.ContentType = new("text/html");
        Assert.AreEqual(ReleaseCheckCode.InvalidResponse, (await Read(wrongType)).Code);
        using var noType = Response(); noType.Content.Headers.ContentType = null;
        Assert.AreEqual(ReleaseCheckCode.InvalidResponse, (await Read(noType)).Code);
        using var vendor = Response(); vendor.Content.Headers.ContentType = new("application/vnd.github+json");
        Assert.AreEqual(ReleaseCheckCode.Ok, (await Read(vendor)).Code);
        using var invalid = Response(); invalid.Content = new ByteArrayContent([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]);
        invalid.Content.Headers.ContentType = new("application/json");
        Assert.AreEqual(ReleaseCheckCode.InvalidResponse, (await Read(invalid)).Code);
    }

    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        internal long ReadBytes;
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); ReadBytes += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); var read = inner.Read(buffer.Span); ReadBytes += read; return ValueTask.FromResult(read); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; inner.Dispose(); base.Dispose(disposing); }
    }

    [TestMethod]
    public async Task StreamIsLimitedEvenWithoutLengthAndDisposedOnFailure()
    {
        var stream = new UnseekableStream(new byte[GitHubReleaseProvider.MaximumBodyBytes + 100]);
        using var response = Response(); response.Content = new StreamContent(stream);
        response.Content.Headers.ContentType = new("application/json");
        Assert.IsNull(response.Content.Headers.ContentLength);
        Assert.AreEqual(ReleaseCheckCode.ResponseTooLarge, (await Read(response)).Code);
        Assert.AreEqual(GitHubReleaseProvider.MaximumBodyBytes + 1L, stream.ReadBytes); Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task ExactBodyLimitIsAcceptedAndKnownOversizeRejected()
    {
        var body = Body(); body += new string(' ', GitHubReleaseProvider.MaximumBodyBytes - Encoding.UTF8.GetByteCount(body));
        Assert.AreEqual(ReleaseCheckCode.Ok, (await Read(Response(body: body))).Code);
        Assert.AreEqual(ReleaseCheckCode.ResponseTooLarge, (await Read(Response(body: body + " "))).Code);
        var stream = new UnseekableStream(Encoding.UTF8.GetBytes(body));
        using var streamed = Response(); streamed.Content = new StreamContent(stream);
        streamed.Content.Headers.ContentType = new("application/json");
        Assert.AreEqual(ReleaseCheckCode.Ok, (await Read(streamed)).Code); Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task HeaderLimitIsEnforcedBeforePayload()
    {
        using var response = Response(); response.Headers.TryAddWithoutValidation("X-Large", new string('x', 16384));
        Assert.AreEqual(ReleaseCheckCode.ResponseTooLarge, (await Read(response)).Code);
    }

    [TestMethod]
    [DataRow("120", 120)] [DataRow("99999999999999999999999999999999999999999999999999", int.MaxValue)]
    [DataRow("bad", 60)] [DataRow("0", 1)] [DataRow("Thu, 01 Jan 1970 00:00:01 GMT", 1)]
    public async Task RetryAfterIsBounded(string value, int expected)
    {
        using var response = Response(429); response.Headers.TryAddWithoutValidation("Retry-After", value);
        var result = await Read(response); Assert.AreEqual(ReleaseCheckCode.RateLimited, result.Code); Assert.AreEqual(expected, result.RetryAfterSeconds);
    }

    [TestMethod]
    public async Task ResetAndRetryAfterUseMaximumAndSecondaryLimitIsRecognized()
    {
        using var response = Response(403); response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1000300");
        response.Headers.TryAddWithoutValidation("Retry-After", "200");
        var result = await Read(response); Assert.AreEqual(ReleaseCheckCode.RateLimited, result.Code); Assert.AreEqual(300, result.RetryAfterSeconds);
        using var secondary = Response(403); secondary.Headers.TryAddWithoutValidation("Retry-After", "120");
        Assert.AreEqual(ReleaseCheckCode.RateLimited, (await Read(secondary)).Code);
        using var successful = Response(); successful.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        successful.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1000400");
        result = await Read(successful); Assert.AreEqual(ReleaseCheckCode.Ok, result.Code); Assert.AreEqual(400, result.RetryAfterSeconds);
    }

    [TestMethod]
    [DataRow(HttpRequestError.SecureConnectionError, ReleaseCheckCode.TlsFailure)]
    [DataRow(HttpRequestError.NameResolutionError, ReleaseCheckCode.NetworkUnavailable)]
    [DataRow(HttpRequestError.ConnectionError, ReleaseCheckCode.NetworkUnavailable)]
    [DataRow(HttpRequestError.ConfigurationLimitExceeded, ReleaseCheckCode.ResponseTooLarge)]
    [DataRow(HttpRequestError.InvalidResponse, ReleaseCheckCode.InvalidResponse)]
    [DataRow(HttpRequestError.HttpProtocolError, ReleaseCheckCode.InvalidResponse)]
    public async Task NetworkExceptionTextIsNotReturned(HttpRequestError error, ReleaseCheckCode expected)
    {
        using var provider = new GitHubReleaseProvider(new Handler((_, _) => throw new HttpRequestException(error, "secret-token")));
        var result = await provider.CheckAsync(Config, CancellationToken.None);
        Assert.AreEqual(expected, result.Code); Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task CancellationPropagatesAndNoHiddenRetryOccurs()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Response(); });
        using var provider = new GitHubReleaseProvider(handler); using var cancellation = new CancellationTokenSource();
        var task = provider.CheckAsync(Config, cancellation.Token); await started.Task; cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task); Assert.AreEqual(1, handler.Calls);
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.CheckAsync(Config, cancellation.Token));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task NumericHeaderOverflowCannotShortenCooldown()
    {
        foreach (var header in new[] { "Retry-After", "X-RateLimit-Reset" })
        {
            using var response = Response(429); response.Headers.TryAddWithoutValidation(header, new string('9', 129));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            Assert.AreEqual(int.MaxValue, (await Read(response)).RetryAfterSeconds);
        }
        using var future = Response(429);
        future.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.FromUnixTimeSeconds(1_000_120));
        Assert.AreEqual(120, (await Read(future)).RetryAfterSeconds);
    }

    [TestMethod]
    public async Task IncorrectLengthCannotBypassStreamingLimit()
    {
        var stream = new UnseekableStream(new byte[GitHubReleaseProvider.MaximumBodyBytes + 50]);
        using var response = Response(); response.Content = new StreamContent(stream);
        response.Content.Headers.ContentType = new("application/json"); response.Content.Headers.ContentLength = 2;
        Assert.AreEqual(ReleaseCheckCode.ResponseTooLarge, (await Read(response)).Code);
        Assert.AreEqual(GitHubReleaseProvider.MaximumBodyBytes + 1L, stream.ReadBytes);
    }

    [TestMethod]
    public async Task FullCSharpPathDefersHttpAndResultUntilPoll()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => { started.SetResult(); return reply.Task; });
        using var provider = new GitHubReleaseProvider(handler, new Clock());
        await using var service = new ReleaseCheckService(Guid.NewGuid(), true, provider, Config);
        var owner = service.Attach(Guid.NewGuid(), 1); var id = service.Check(owner);
        Assert.AreEqual(0, handler.Calls); service.Poll(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        reply.SetResult(Response());
        Assert.IsFalse(service.TryTakeResult(owner, out _));
        for (var i = 0; i < 3000 && service.HasRunningTransport; i++) { await Task.Delay(1); service.Poll(); }
        Assert.IsTrue(service.TryTakeResult(owner, out var result)); Assert.IsNotNull(result); Assert.AreEqual(id, result.RequestId); Assert.IsTrue(result.Success);
        service.CloseGeneration(1); var next = service.Attach(Guid.NewGuid(), 2);
        service.Check(next); Assert.IsTrue(service.TryTakeResult(next, out result)); Assert.IsNotNull(result); Assert.IsTrue(result.Release!.FromCache);
        Assert.AreEqual(1, handler.Calls);
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("body-read-failure"));
    }

    [TestMethod]
    [DataRow(404, "normal", ReleaseCheckCode.NotFound)]
    [DataRow(401, "normal", ReleaseCheckCode.AccessDenied)]
    [DataRow(503, "normal", ReleaseCheckCode.ProviderUnavailable)]
    [DataRow(302, "normal", ReleaseCheckCode.InvalidResponse)]
    [DataRow(200, "version", ReleaseCheckCode.InvalidVersion)]
    [DataRow(200, "json", ReleaseCheckCode.InvalidResponse)]
    [DataRow(200, "fields", ReleaseCheckCode.InvalidResponse)]
    [DataRow(200, "media", ReleaseCheckCode.InvalidResponse)]
    [DataRow(200, "size", ReleaseCheckCode.ResponseTooLarge)]
    [DataRow(200, "read", ReleaseCheckCode.NetworkUnavailable)]
    public async Task ErrorReplyPreservesRateLimitAndHostBlocksUntilReset(int status, string failure, ReleaseCheckCode expected)
    {
        HttpResponseMessage MakeResponse()
        {
            var response = Response(status, failure switch
            {
                "version" => Body("v1.10.0-beta"),
                "json" => "{broken-json",
                "fields" => "{}",
                "size" => new string(' ', GitHubReleaseProvider.MaximumBodyBytes + 1),
                _ => Body()
            });
            if (failure == "media") response.Content.Headers.ContentType = new("text/html");
            if (failure == "read")
            { response.Content = new UnreadableContent(); response.Content.Headers.ContentType = new("application/json"); }
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1003600");
            return response;
        }

        var adapterReply = await Read(MakeResponse());
        Assert.AreEqual(expected, adapterReply.Code);
        Assert.AreEqual(3600, adapterReply.RetryAfterSeconds);
        Assert.IsNull(adapterReply.Release);

        var clock = new Clock();
        var handler = new Handler((_, _) => Task.FromResult(MakeResponse()));
        using var provider = new GitHubReleaseProvider(handler, clock);
        await using var service = new ReleaseCheckService(Guid.NewGuid(), false, provider, Config, clock);
        var owner = service.Attach(Guid.NewGuid(), 1);
        async Task Drain()
        {
            service.Poll();
            for (var i = 0; i < 3000 && service.HasRunningTransport; i++) { await Task.Delay(1); service.Poll(); }
            Assert.IsFalse(service.HasRunningTransport);
        }
        service.Check(owner); await Drain();
        Assert.IsTrue(service.TryTakeResult(owner, out var error)); Assert.IsNotNull(error);
        Assert.AreEqual(expected, error.Code); Assert.IsNull(error.Release); Assert.AreEqual(1, handler.Calls);
        foreach (var (advance, remaining) in new[] { (61, 3539), (3538, 1) })
        {
            clock.Advance(advance); service.Check(owner); service.Poll();
            Assert.IsTrue(service.TryTakeResult(owner, out var blocked)); Assert.IsNotNull(blocked);
            Assert.AreEqual(ReleaseCheckCode.RateLimited, blocked.Code); Assert.AreEqual(remaining, blocked.RetryAfterSeconds);
            Assert.AreEqual(1, handler.Calls);
        }
        clock.Advance(1); service.Check(owner); await Drain();
        Assert.AreEqual(2, handler.Calls);
    }
}
