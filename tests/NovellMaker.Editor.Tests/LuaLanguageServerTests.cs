using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Tests;

[TestClass]
public sealed class LuaLanguageServerTests
{
    private const string ValidSource = """
        ---@type NMHandlers
        local handlers = {
          on_load = function() ui.log('info', 'ready') end,
          ---@param e NMEvent
          on_event = function(e)
            ui.set_text(refs.label, e.kind)
            ui.set_geometry(refs.label, {x=10, opacity=0.5})
          end
        }
        return handlers
        """;

    [TestMethod]
    [TestCategory("LuaLanguageServer")]
    public async Task LuaLsChecksInstalledAnnotationsAndRejectsBrokenDocsAndTypes()
    {
        using var fixture = new Workspace();
        var validFile = Path.Combine(fixture.Root, "valid.lua"); await File.WriteAllTextAsync(validFile, ValidSource);
        var exampleFile = Path.Combine(fixture.Root, "example.lua");
        // Keep the actual annotated example intact, including its deliberately broken runtime call.
        await File.WriteAllTextAsync(exampleFile, ExampleLuaDiagnosticsModule.Source);
        var first = await fixture.Check("positive");
        var intentional = first.Report.EnumerateObject().SelectMany(file => file.Value.EnumerateArray().Select(d => (file.Name, Diagnostic: d))).ToArray();
        Assert.IsTrue(intentional.All(item => new Uri(item.Name).LocalPath.EndsWith("example.lua", StringComparison.Ordinal) &&
            item.Diagnostic.GetProperty("code").GetString() == "need-check-nil"), first.Report.ToString());
        Assert.HasCount(1, intentional, "Only the deliberate nil call in the example may be diagnosed.");
        Assert.AreEqual(ErrorLine() - 1, intentional[0].Diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        File.Delete(exampleFile);
        var clean = await fixture.Check("clean"); Assert.AreEqual(0, clean.ExitCode, clean.Report.ToString());
        Assert.IsTrue(clean.Report.ValueKind == JsonValueKind.Array ? clean.Report.GetArrayLength() == 0 : !clean.Report.EnumerateObject().Any());

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "broken.lua"), """
            ---@type NMHandlers
            local handlers = {
              ---@param e NMEvent
              on_event = function(e)
                ui.log('debug', 42)
                ui.set_geometry(refs.label, {opacity='bad'})
                local value = e.not_a_real_field
                ui.set_text(refs.label, value)
              end
            }
            ---@type MissingNovelType
            local missing = {}
            ---@param broken table<
            local function invalid(broken) return broken end
            invalid(missing)
            return handlers
            """);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "Scripts", "NovellMakerApi", "ui.lua"), "\n---@alias BrokenMeta table<\n");
        var broken = await fixture.Check("negative"); Assert.AreNotEqual(0, broken.ExitCode);
        var diagnostics = broken.Report.EnumerateObject().SelectMany(file => file.Value.EnumerateArray()).ToArray();
        var codes = diagnostics.Select(d => d.GetProperty("code").GetString()).ToArray();
        foreach (var code in new[] { "luadoc-miss-type-name", "undefined-doc-name", "undefined-field", "param-type-mismatch", "assign-type-mismatch" })
            CollectionAssert.Contains(codes, code, broken.Report.ToString());
        Assert.IsTrue(diagnostics.Any(d => d.GetProperty("code").GetString() == "undefined-field" && d.GetProperty("message").GetString()!.Contains("not_a_real_field")));
        Assert.IsTrue(broken.Report.EnumerateObject().Any(file => new Uri(file.Name).LocalPath.EndsWith("ui.lua", StringComparison.Ordinal) &&
            file.Value.EnumerateArray().Any(d => d.GetProperty("code").GetString() == "luadoc-miss-type-name")), "LuaLS must validate installed @meta comments too.");
    }

    internal static int ErrorLine() => ExampleLuaDiagnosticsModule.Source.Split('\n').Select((line, index) => (line, index))
        .Single(item => item.line.Trim() == "missing()").index + 1;

    [TestMethod]
    [TestCategory("LuaLanguageServer")]
    public async Task LuaLsOffersEventCompletionHoverAndLogSignatureOverRealLsp()
    {
        using var fixture = new Workspace(); var file = Path.Combine(fixture.Root, "completion.lua");
        var source = ExampleLuaDiagnosticsModule.Source;
        await File.WriteAllTextAsync(file, source); var uri = new Uri(file).AbsoluteUri;
        await using var client = new LspClient(fixture.Start("--logpath=" + Path.Combine(fixture.DirectoryPath, "lsp-log"),
            "--metapath=" + Path.Combine(fixture.DirectoryPath, "meta")));
        var rootUri = new Uri(fixture.Root + Path.DirectorySeparatorChar).AbsoluteUri;
        var initialized = await client.Request("initialize", new
        {
            processId = (int?)null, rootUri,
            capabilities = new { textDocument = new { completion = new { completionItem = new { snippetSupport = true } }, hover = new { contentFormat = new[] { "markdown" } } } },
            workspaceFolders = new[] { new { uri = rootUri, name = "NovellMaker fixture" } },
        });
        Assert.AreEqual("3.19.2", initialized.GetProperty("serverInfo").GetProperty("version").GetString());
        await client.Notify("initialized", new { });
        await client.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "lua", version = 1, text = source } });
        var lines = source.Split('\n');
        var eventLine = Array.FindIndex(lines, line => line.Contains("e.kind", StringComparison.Ordinal));
        var logLine = Array.FindIndex(lines, line => line.Contains("ui.log(", StringComparison.Ordinal));
        var declarationLine = Array.FindIndex(lines, line => line.Contains("local handlers =", StringComparison.Ordinal));
        JsonElement completion = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            completion = await client.Request("textDocument/completion", new { textDocument = new { uri },
                position = new { line = eventLine, character = lines[eventLine].IndexOf("e.kind", StringComparison.Ordinal) + 2 }, context = new { triggerKind = 1 } });
            if (completion.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) break;
            await Task.Delay(50);
        }
        Assert.IsTrue(completion.ValueKind is JsonValueKind.Array or JsonValueKind.Object, "LuaLS did not finish loading the workspace.");
        var items = completion.ValueKind == JsonValueKind.Array ? completion : completion.GetProperty("items");
        var labels = items.EnumerateArray().Select(item => item.GetProperty("label").GetString()).ToArray();
        foreach (var label in new[] { "kind", "node", "seconds", "request_id" }) CollectionAssert.Contains(labels, label);
        CollectionAssert.DoesNotContain(labels, "not_a_real_field");
        var handlers = await client.Request("textDocument/hover", new { textDocument = new { uri },
            position = new { line = declarationLine, character = lines[declarationLine].IndexOf("handlers", StringComparison.Ordinal) + 2 } });
        StringAssert.Contains(handlers.GetProperty("contents").GetProperty("value").GetString()!, "handlers: NMHandlers");
        var hover = await client.Request("textDocument/hover", new { textDocument = new { uri },
            position = new { line = logLine, character = lines[logLine].IndexOf("ui.log", StringComparison.Ordinal) + 4 } });
        var hoverText = hover.GetProperty("contents").GetProperty("value").GetString()!;
        StringAssert.Contains(hoverText, "function ui.log(level:"); StringAssert.Contains(hoverText, "message: string");
        var help = await client.Request("textDocument/signatureHelp", new { textDocument = new { uri },
            position = new { line = logLine, character = lines[logLine].IndexOf(",'", StringComparison.Ordinal) + 3 } });
        var signature = help.GetProperty("signatures")[0]; Assert.AreEqual(1, signature.GetProperty("activeParameter").GetInt32());
        StringAssert.Contains(signature.GetProperty("label").GetString()!, "'error'|'info'|'warning'");
        Assert.AreEqual(2, signature.GetProperty("parameters").GetArrayLength());
        await client.Request("shutdown", null); await client.Notify("exit", null);
    }

    /// <summary>Small stdio JSON-RPC peer; no mocked LuaLS responses or IDE/global configuration.</summary>
    private sealed class LspClient(Process process) : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();
        private int sequence;
        internal Task Notify(string method, object? parameters) => Send(new { jsonrpc = "2.0", method, @params = parameters });
        internal async Task<JsonElement> Request(string method, object? parameters)
        {
            var id = ++sequence; await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (true)
            {
                var response = await Read();
                if (!response.TryGetProperty("id", out var receivedId)) continue;
                if (response.TryGetProperty("method", out _))
                { await Send(new { jsonrpc = "2.0", id = receivedId.Clone(), result = (object?)null }); continue; }
                if (receivedId.GetInt32() != id) continue;
                Assert.IsFalse(response.TryGetProperty("error", out _), response.ToString());
                return response.GetProperty("result").Clone();
            }
        }
        private async Task Send(object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), timeout.Token);
            await input.WriteAsync(bytes, timeout.Token); await input.FlushAsync(timeout.Token);
        }
        private async Task<JsonElement> Read()
        {
            var output = process.StandardOutput.BaseStream;
            var header = new List<byte>(); var one = new byte[1];
            while (true)
            {
                await output.ReadExactlyAsync(one, timeout.Token); header.Add(one[0]);
                if (header.Count > 4096) throw new IOException("Oversized LSP header.");
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var lengthHeader = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var count = int.Parse(lengthHeader.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            if (count is < 1 or > 1024 * 1024) throw new IOException("Oversized LSP response.");
            var bytes = new byte[count]; await output.ReadExactlyAsync(bytes, timeout.Token);
            using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); await stderr;
            }
            finally { process.Dispose(); timeout.Dispose(); }
        }
    }

    private sealed class Workspace : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "NovellMaker.LuaLS", Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(DirectoryPath, "workspace");
        internal string Server { get; }
        internal Workspace()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            Server = Path.Combine(repo.FullName, "artifacts", "build-tools", "luals-3.19.2", "bin", "lua-language-server.exe");
            if (!OperatingSystem.IsWindows() || !File.Exists(Server))
                Assert.Inconclusive("Prepare the pinned real LuaLS: pwsh scripts/check.ps1 -LuaLanguageServer (Windows x64).");
            Directory.CreateDirectory(Root);
            var readme = File.ReadAllText(LuaAuthoringTools.Install(Root));
            var config = JsonNode.Parse(Regex.Match(readme, @"```json\s*(.*?)```", RegexOptions.Singleline).Groups[1].Value)!.AsObject();
            config["diagnostics.libraryFiles"] = "Enable"; // Validate @meta declarations too, not just their consumers.
            File.WriteAllText(Path.Combine(Root, ".luarc.json"), config.ToJsonString());
        }

        internal Process Start(params string[] args)
        {
            var start = new ProcessStartInfo(Server) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new IOException("LuaLS did not start.");
        }

        internal async Task<(int ExitCode, JsonElement Report)> Check(string run)
        {
            var logs = Path.Combine(DirectoryPath, run);
            using var process = Start("--check=" + Root, "--logpath=" + logs, "--metapath=" + Path.Combine(DirectoryPath, "meta"), "--check_format=json", "--checklevel=Information");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("LuaLS diagnosis timed out."); }
            var output = await stdout; var errors = await stderr;
            var reportPath = Path.Combine(logs, "check.json");
            Assert.IsTrue(File.Exists(reportPath), $"LuaLS exited {process.ExitCode} without a report: {output}\n{errors}");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            return (process.ExitCode, json.RootElement.Clone());
        }

        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
