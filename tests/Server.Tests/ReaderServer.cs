using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Server.Tests;

// One reader server for all tests, which run one at a time (they share it, so each works with its own feeds), plus a way to start
// extra empty ones for round trips.
[CollectionDefinition("reader")]
public class ReaderCollection : ICollectionFixture<ReaderFixture>;

public sealed class ReaderFixture : IAsyncLifetime
{
    public FeedSite Site { get; } = new();
    public ReaderServer Reader { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Site.StartAsync();
        Reader = await ReaderServer.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        await Site.DisposeAsync();
    }
}

// The real server: server/reader.cs, built once per test run and started against a temporary database
public sealed class ReaderServer : IAsyncDisposable
{
    static readonly SemaphoreSlim buildLock = new(1);
    static string? builtDll;

    readonly Process process;
    readonly StringBuilder output = new();
    public string DataDir { get; }
    public HttpClient Http { get; }

    ReaderServer(Process process, string dataDir, int port)
    {
        this.process = process;
        DataDir = dataDir;
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "server", "reader.cs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Can't find the repository root");
    }

    static async Task<string> Build()
    {
        await buildLock.WaitAsync();
        try
        {
            if (builtDll is not null) return builtDll;
            var outDir = Path.Combine(Path.GetTempPath(), "reader-tests-build");
            var build = Process.Start(new ProcessStartInfo("dotnet", ["build", Path.Combine(RepoRoot, "server", "reader.cs"), "-o", outDir, "-nologo", "-v", "q"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var log = await build.StandardOutput.ReadToEndAsync() + await build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) throw new InvalidOperationException("Building reader.cs failed:\n" + log);
            return builtDll = Path.Combine(outDir, "reader.dll");
        }
        finally
        {
            buildLock.Release();
        }
    }

    public static async Task<ReaderServer> StartAsync(Dictionary<string, string>? env = null)
    {
        var dll = await Build();
        var dataDir = Directory.CreateTempSubdirectory("reader-tests-").FullName;
        var port = FreePort();
        var info = new ProcessStartInfo("dotnet", [dll])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dataDir,
            Environment =
            {
                ["READER_DB"] = Path.Combine(dataDir, "reader.db"),
                ["READER_HOST"] = "127.0.0.1",
                ["READER_PORT"] = port.ToString(),
                ["READER_WEB"] = Path.Combine(dataDir, "web"),
                ["PWD"] = dataDir,
            },
        };
        foreach (var (k, v) in env ?? []) info.Environment[k] = v;
        var server = new ReaderServer(Process.Start(info)!, dataDir, port);
        server.process.OutputDataReceived += (_, e) => { lock (server.output) server.output.AppendLine(e.Data); };
        server.process.ErrorDataReceived += (_, e) => { lock (server.output) server.output.AppendLine(e.Data); };
        server.process.BeginOutputReadLine();
        server.process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (server.process.HasExited) throw new InvalidOperationException("reader.cs exited:\n" + server.Output);
            try
            {
                if ((await server.Http.GetAsync("/api/status")).IsSuccessStatusCode) return server;
            }
            catch (HttpRequestException) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("reader.cs didn't start:\n" + server.Output);
            await Task.Delay(100);
        }
    }

    public string Output { get { lock (output) return output.ToString(); } }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    // ---- API helpers

    public async Task<JsonElement> Get(string url)
    {
        var res = await Http.GetAsync(url);
        await EnsureOk(res);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> GetText(string url)
    {
        var res = await Http.GetAsync(url);
        await EnsureOk(res);
        return await res.Content.ReadAsStringAsync();
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is string raw) req.Content = new StringContent(raw);
        else if (body is not null) req.Content = JsonContent.Create(body);
        return await Http.SendAsync(req);
    }

    public async Task<JsonElement> Post(string url, object? body = null) => await Json(await Send(HttpMethod.Post, url, body ?? new { }));
    public async Task<JsonElement> Patch(string url, object body) => await Json(await Send(HttpMethod.Patch, url, body));
    public async Task Delete(string url) => await EnsureOk(await Send(HttpMethod.Delete, url));

    static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        await EnsureOk(res);
        return res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0
            ? default
            : await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task EnsureOk(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)res.StatusCode} {res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: {await res.Content.ReadAsStringAsync()}");
    }

    public async Task<JsonElement> Subscribe(string url, long? folderId = null) => await Post("/api/feeds", new { url, folderId });

    public async Task<JsonElement> Feed(long id)
    {
        var subs = await Get("/api/subscriptions");
        return subs.GetProperty("feeds").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == id);
    }

    public async Task<List<JsonElement>> Items(string stream, string query = "")
    {
        var d = await Get($"/api/items?stream={stream}&sort=oldest&limit=200{query}");
        return d.GetProperty("items").EnumerateArray().ToList();
    }

    // Waits for something the background services do
    public static async Task Eventually(Func<Task<bool>> condition, int seconds = 20, string what = "condition")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }
}

// A local web site for the server to fetch from: the sample feeds in feeds/ (with {{base}} replaced by
// this site's address), pictures, and addresses that behave like moved or removed feeds.
public sealed class FeedSite : IAsyncDisposable
{
    WebApplication? app;
    public string Base { get; private set; } = "";
    public static string FeedsDir { get; } = Path.Combine(AppContext.BaseDirectory, "feeds");

    // A 1x1 PNG
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // Feeds made up by a test, served at /made/<name>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Made { get; } = [];

    // How often each made-up feed was asked for
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Requests { get; } = [];

    const string B = "{{base}}";

    // A minimal RSS feed with the given item titles, for tests that need a feed of their own
    public static string Rss(string title, params string[] items) => $"""
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>{title}</title><link>{B}/</link>
        {string.Concat(items.Select((t, i) => $"<item><title>{t}</title><guid>{title}-{i}</guid><link>{B}/{Uri.EscapeDataString(title)}/{i}</link><pubDate>Mon, 0{i % 9 + 1} Jan 2024 10:00:00 GMT</pubDate><description>Post {t}</description></item>"))}
        </channel></rss>
        """;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/feeds/{name}", (string name) =>
        {
            var path = Path.Combine(FeedsDir, name);
            if (!File.Exists(path) || name.Contains("..")) return Results.NotFound();
            var type = name.EndsWith(".html") ? "text/html" : name.EndsWith(".json") ? "application/json" : "application/xml";
            return Results.Text(Fill(File.ReadAllText(path)), type);
        });
        // "gone" answers 410 Gone, "moved:<name>" redirects permanently to /made/<name>
        app.MapGet("/made/{name}", (string name, HttpResponse res) =>
        {
            Requests.AddOrUpdate(name, 1, (_, n) => n + 1);
            if (!Made.TryGetValue(name, out var text)) return Results.NotFound();
            if (text == "gone") return Results.StatusCode(410);
            if (text.StartsWith("moved:"))
            {
                res.Headers.Location = Base + "/made/" + text[6..];
                return Results.StatusCode(301);
            }
            return Results.Text(Fill(text), "application/xml");
        });
        // pictures whose names start with "ok-" exist; any other is missing
        app.MapGet("/images/{name}", (string name) => name.StartsWith("ok-") ? Results.Bytes(Png, "image/png") : Results.NotFound());
        await app.StartAsync();
        Base = app.Urls.First().TrimEnd('/');
    }

    public string Fill(string text) => text.Replace("{{base}}", Base);

    public string Url(string path) => Base + path;

    public async ValueTask DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
    }
}
