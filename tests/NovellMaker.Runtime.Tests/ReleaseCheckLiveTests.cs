using System.Text.Json;
using NovellMaker.Core.ExternalServices;
using NovellMaker.Runtime.ExternalServices;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ReleaseCheckLiveTests
{
    [TestMethod]
    [TestCategory("LiveNetwork")]
    public async Task RealGitHubLatestReleaseThroughUnmodifiedAdapter()
    {
        if (Environment.GetEnvironmentVariable("NOVELLMAKER_RELEASE_LIVE_SMOKE") != "1")
            Assert.Inconclusive("Opt in to a public unauthenticated GitHub GET with NOVELLMAKER_RELEASE_LIVE_SMOKE=1.");
        using var provider = new GitHubReleaseProvider();
        using var deadline = new CancellationTokenSource(ReleaseCheckService.Deadline);
        var reply = await provider.CheckAsync(new("dotnet", "runtime", "1.0.0"), deadline.Token);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        var directory = Path.Combine(repo!.FullName, "artifacts", "screenshots", "release-check");
        Directory.CreateDirectory(directory);
        var evidence = JsonSerializer.Serialize(new { Endpoint = "https://api.github.com/repos/dotnet/runtime/releases/latest",
            ObservedAtUtc = DateTimeOffset.UtcNow, Reply = reply }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "github-live.json"), evidence);
        Assert.AreEqual(ReleaseCheckCode.Ok, reply.Code, evidence);
        Assert.IsNotNull(reply.Release); Assert.IsTrue(reply.Release.UpdateAvailable);
        Assert.IsFalse(reply.Release.FromCache);
        StringAssert.StartsWith(reply.Release.PageUrl, "https://github.com/dotnet/runtime/releases/tag/");
    }
}
