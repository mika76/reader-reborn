#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Data.Sqlite@10.0.12
#:package Dapper@2.1.89
#:property PublishAot=false
#:property InvariantGlobalization=true

// Reader Reborn backend: a single-file .NET app that keeps subscriptions in SQLite, downloads
// feeds on a schedule and serves the JSON API plus the built Vue frontend (web/dist).
//
//   ./reader.cs                  serve on http://localhost:5199 and fetch feeds in the background
//   ./reader.cs fetch            fetch every feed once and exit (cron-friendly)
//   ./reader.cs add <url>        subscribe to a feed or a site (autodiscovery)
//   ./reader.cs import <x.opml>  import subscriptions from an OPML file
//
// Environment: READER_DB (sqlite path, default data/reader.db), READER_PORT (default 5199),
// READER_INTERVAL (minutes between fetches of one feed, default 30), READER_IMAGES (set to "off" to
// stop downloading post images; they are kept in a second database, reader-media.db, next to the first),
// READER_HOST (address to listen on, default localhost; 0.0.0.0 in a container), READER_WEB (the built
// frontend, default web/dist).

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Dapper;
using Microsoft.Data.Sqlite;

var scriptDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
// `dotnet run` starts file-based apps in the script's folder; PWD is still the folder the user ran it from
var userDir = Environment.GetEnvironmentVariable("PWD") ?? Directory.GetCurrentDirectory();
string UserPath(string path) => Path.GetFullPath(path, userDir);
var root = Path.GetFullPath(Path.Combine(scriptDir, ".."));
var dbPath = Environment.GetEnvironmentVariable("READER_DB") is { } envDb ? UserPath(envDb) : Path.Combine(root, "data", "reader.db");
Db.Init(dbPath);
var dataDir = Path.GetDirectoryName(dbPath)!;
Media.Init(Path.Combine(dataDir, Path.GetFileNameWithoutExtension(dbPath) + "-media.db"));
ImageCache.Init(Path.Combine(dataDir, "images"), Environment.GetEnvironmentVariable("READER_IMAGES") != "off");
ImageCache.UpgradeStorage();
Attachments.Init(dataDir);

switch (args.FirstOrDefault())
{
    case "--version" or "version":
        Console.WriteLine($"Reader Reborn {AppInfo.Version}");
        return;
    case "fetch":
        var fetched = await Fetcher.FetchDue(force: true);
        Console.WriteLine($"Fetched {fetched.Feeds} feeds, {fetched.NewItems} new items");
        if (ImageCache.Enabled)
        {
            var (saved, failed) = (0, 0);
            while (await ImageCache.RunOnce(CancellationToken.None) is var r && r.Busy)
                (saved, failed) = (saved + r.Downloaded, failed + r.Failed);
            Console.WriteLine($"Saved {saved} images, {failed} failed");
        }
        return;
    case "add" when args.Length > 1:
        var added = await Fetcher.Subscribe(args[1], null);
        Console.WriteLine($"Subscribed to {added.Title} ({added.Url})");
        return;
    case "import" when args.Length > 1:
        Console.WriteLine($"Imported {Opml.Import(File.ReadAllText(UserPath(args[1])))} feeds");
        await Fetcher.FetchDue(force: false);
        return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = root,
    WebRootPath = Environment.GetEnvironmentVariable("READER_WEB") is { } web ? UserPath(web) : Path.Combine(root, "web", "dist"),
});
var listen = $"http://{Environment.GetEnvironmentVariable("READER_HOST") ?? "localhost"}:{Environment.GetEnvironmentVariable("READER_PORT") ?? "5199"}";
builder.WebHost.UseUrls(listen);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddHostedService<FetchService>();
builder.Services.AddHostedService<ImageService>();
builder.Services.AddHostedService<AttachmentService>();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/subscriptions", () =>
{
    using var db = Db.Open();
    var folders = db.Query<FolderRow>("SELECT id, name, sort FROM folders ORDER BY name COLLATE NOCASE");
    var feeds = db.Query<FeedRow>("""
        SELECT f.id, COALESCE(f.custom_title, f.title, f.url) AS title, f.url, f.site_url, f.sort,
               f.last_fetched, f.last_error, f.icon IS NOT NULL AS has_icon,
               (SELECT COUNT(*) FROM items i WHERE i.feed_id = f.id AND i.is_read = 0) AS unread
        FROM feeds f WHERE f.is_subscribed = 1 ORDER BY title COLLATE NOCASE
        """).ToList();
    var memberships = db.Query<(long FeedId, long FolderId, long Sort)>("SELECT feed_id, folder_id, sort FROM feed_folders").ToLookup(m => m.FeedId);
    foreach (var feed in feeds)
    {
        feed.FolderIds = memberships[feed.Id].Select(m => m.FolderId).ToList();
        feed.FolderSort = memberships[feed.Id].ToDictionary(m => m.FolderId, m => m.Sort);
    }
    var starred = db.ExecuteScalar<long>("SELECT COUNT(*) FROM items WHERE is_starred = 1");
    return new { folders, feeds, starred };
});

api.MapGet("/items", (string? stream, bool? unread, string? sort, long? cursorTs, long? cursorId, int? limit, string? q) =>
{
    using var db = Db.Open();
    var (where, p) = Streams.Filter(stream, q);
    if (unread == true) where.Add("i.is_read = 0");
    var oldest = sort == "oldest";
    if (cursorTs is not null && cursorId is not null)
    {
        where.Add(oldest
            ? "(i.published > @cursorTs OR (i.published = @cursorTs AND i.id > @cursorId))"
            : "(i.published < @cursorTs OR (i.published = @cursorTs AND i.id < @cursorId))");
        p.Add("cursorTs", cursorTs);
        p.Add("cursorId", cursorId);
    }
    var take = Math.Clamp(limit ?? 40, 1, 200);
    p.Add("take", take + 1);
    var dir = oldest ? "ASC" : "DESC";
    var items = db.Query<ItemRow>($"""
        SELECT i.id, i.feed_id, COALESCE(f.custom_title, f.title, f.url) AS feed_title, i.title, i.url, i.author,
               i.content, i.summary, i.published, i.is_read, i.is_starred
        FROM items i JOIN feeds f ON f.id = i.feed_id
        {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
        ORDER BY i.published {dir}, i.id {dir} LIMIT @take
        """, p).ToList();
    var more = items.Count > take;
    if (more) items.RemoveAt(take);
    var enclosures = db.Query<EnclosureRow>("SELECT item_id, url, type, length, hash FROM enclosures WHERE item_id IN @ids",
        new { ids = items.Select(i => i.Id) }).ToLookup(e => e.ItemId);
    foreach (var item in items) item.Enclosures = enclosures[item.Id].ToList();
    ImageCache.UseLocalCopies(db, items);
    var last = items.LastOrDefault();
    return new
    {
        items,
        continuation = more && last is not null ? new { ts = last.Published, id = last.Id } : null,
        maxId = db.ExecuteScalar<long>("SELECT COALESCE(MAX(id), 0) FROM items"),
    };
});

api.MapPost("/items/{id:long}", (long id, ItemPatch body) =>
{
    using var db = Db.Open();
    if (body.Read is bool read) db.Execute("UPDATE items SET is_read = @read WHERE id = @id", new { id, read });
    if (body.Starred is bool starred) db.Execute("UPDATE items SET is_starred = @starred WHERE id = @id", new { id, starred });
    return Results.NoContent();
});

api.MapPost("/items/mark-read", (MarkRead body) =>
{
    using var db = Db.Open();
    var (where, p) = Streams.Filter(body.Stream, null);
    where.Add("i.is_read = 0");
    where.Add("i.id <= @maxId");
    p.Add("maxId", body.MaxId ?? long.MaxValue);
    if (body.OlderThanDays is int days)
    {
        where.Add("i.published < @cutoff");
        p.Add("cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());
    }
    var n = db.Execute($"""
        UPDATE items SET is_read = 1 WHERE id IN (
          SELECT i.id FROM items i JOIN feeds f ON f.id = i.feed_id WHERE {string.Join(" AND ", where)})
        """, p);
    return Results.Ok(new { marked = n });
});

api.MapPost("/feeds", async (Subscribe body) =>
{
    try
    {
        var feed = await Fetcher.Subscribe(body.Url, body.FolderId);
        Activity.Log("subscribe", $"Subscribed to {feed.Title}");
        return Results.Ok(feed);
    }
    catch (Exception ex)
    {
        Activity.Log("error", $"Couldn't subscribe to {body.Url}: {ex.Message}");
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPatch("/feeds/{id:long}", (long id, FeedPatch body) =>
{
    using var db = Db.Open();
    if (body.Title is not null)
        db.Execute("UPDATE feeds SET custom_title = NULLIF(TRIM(@title), '') WHERE id = @id", new { id, title = body.Title });
    // moving between folders sends both
    if (body.RemoveFolder is long from)
        db.Execute("DELETE FROM feed_folders WHERE feed_id = @id AND folder_id = @from", new { id, from });
    if (body.AddFolder is long to)
        db.Execute("INSERT OR IGNORE INTO feed_folders (feed_id, folder_id) VALUES (@id, @to)", new { id, to });
    return Results.NoContent();
});

api.MapDelete("/feeds/{id:long}", (long id) =>
{
    using var db = Db.Open();
    Subscriptions.Unsubscribe(db, id);
    return Results.NoContent();
});

// "More actions..." for the checked subscriptions in Settings
api.MapPost("/feeds/bulk", (BulkBody body) =>
{
    if (body.Action is not ("unsubscribe" or "addFolder" or "removeFolder") || (body.Action != "unsubscribe" && body.FolderId is null))
        return Results.BadRequest(new { error = "Unknown action" });
    using var db = Db.Open();
    using var tx = db.BeginTransaction();
    foreach (var id in body.Ids)
    {
        if (body.Action == "unsubscribe")
            Subscriptions.Unsubscribe(db, id, tx);
        else
            db.Execute(body.Action == "addFolder"
                ? "INSERT OR IGNORE INTO feed_folders (feed_id, folder_id) VALUES (@id, @FolderId)"
                : "DELETE FROM feed_folders WHERE feed_id = @id AND folder_id = @FolderId", new { id, body.FolderId }, tx);
    }
    tx.Commit();
    if (body.Action == "unsubscribe") Activity.Log("subscribe", $"Unsubscribed from {body.Ids.Count} subscriptions");
    return Results.Ok(new { changed = body.Ids.Count });
});

api.MapGet("/images/{id:long}", (long id, HttpResponse res) =>
{
    using var db = Db.Open();
    var image = db.QuerySingleOrDefault<ImageRow>("SELECT id, url, file, type, hash FROM images WHERE id = @id", new { id });
    var stored = image?.Hash is { } hash ? Media.Get(hash) : null;
    if (stored is null && image?.File is null) return Results.NotFound();
    SafeMedia(res);
    res.Headers.CacheControl = "public, max-age=31536000, immutable";
    // files from before images were kept in reader-media.db, until they are moved in
    return stored is not null
        ? Results.Bytes(stored.Data, stored.Type)
        : Results.File(ImageCache.FullPath(image!.File!), image.Type ?? "application/octet-stream");
});

api.MapGet("/settings", () => new { saveStarredAttachments = Settings.SaveStarredAttachments });

api.MapPatch("/settings", (SettingsBody body) =>
{
    if (body.SaveStarredAttachments is bool on)
    {
        Settings.SaveStarredAttachments = on;
        Activity.Log("attachments", on ? "Saving attachments of starred posts" : "Stopped saving attachments of starred posts");
        AttachmentService.Kick();
    }
    return Results.Ok(new { saveStarredAttachments = Settings.SaveStarredAttachments });
});

// Saved attachments, streamed with range support so audio and video can seek
api.MapGet("/media/{hash}", (string hash, HttpResponse res) =>
{
    var stream = Media.OpenRead(hash, out var type);
    if (stream is null) return Results.NotFound();
    SafeMedia(res);
    res.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Stream(stream, type, enableRangeProcessing: true);
});

api.MapGet("/images/stats", () =>
{
    using var db = Db.Open();
    return ImageCache.Stats(db);
});

// Reader's Trends page: the busiest subscriptions over the last 30 days, and the ones with nothing new
api.MapGet("/trends", () =>
{
    using var db = Db.Open();
    var since = Db.Now - 30 * 86400;
    return new
    {
        subscriptions = db.ExecuteScalar<long>("SELECT COUNT(*) FROM feeds WHERE is_subscribed = 1"),
        items = db.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM items i JOIN feeds f ON f.id = i.feed_id WHERE f.is_subscribed = 1 AND i.published >= @since
            """, new { since }),
        mostActive = db.Query<TrendRow>("""
            SELECT f.id, COALESCE(f.custom_title, f.title, f.url) AS title, COUNT(*) AS items, MAX(i.published) AS last_item
            FROM feeds f JOIN items i ON i.feed_id = f.id AND i.published >= @since
            WHERE f.is_subscribed = 1 GROUP BY f.id ORDER BY items DESC LIMIT 40
            """, new { since }),
        inactive = db.Query<TrendRow>("""
            SELECT f.id, COALESCE(f.custom_title, f.title, f.url) AS title, 0 AS items, MAX(i.published) AS last_item
            FROM feeds f LEFT JOIN items i ON i.feed_id = f.id
            WHERE f.is_subscribed = 1 GROUP BY f.id HAVING last_item IS NULL OR last_item < @since
            ORDER BY last_item, title COLLATE NOCASE
            """, new { since }),
    };
});

api.MapGet("/status", () =>
{
    using var db = Db.Open();
    long FileSize(string path) => new[] { path, path + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
    var now = Db.Now;
    return new
    {
        server = new
        {
            started = Activity.Started.ToUnixTimeSeconds(),
            version = AppInfo.Version,
            dotnet = Environment.Version.ToString(),
            memory = Environment.WorkingSet,
            database = dbPath,
            databaseBytes = FileSize(dbPath),
            mediaBytes = FileSize(Media.Path),
            intervalMinutes = Fetcher.IntervalMinutes,
            imagesEnabled = ImageCache.Enabled,
        },
        fetching = Activity.CurrentPass,
        lastPass = Activity.LastPass,
        feeds = db.QuerySingle<FeedStats>("""
            SELECT COUNT(*) AS subscribed,
                   COUNT(*) FILTER (WHERE last_error IS NOT NULL AND retired_at IS NULL) AS failing,
                   COUNT(*) FILTER (WHERE retired_at IS NOT NULL) AS retired,
                   COUNT(*) FILTER (WHERE next_fetch <= @now AND retired_at IS NULL) AS due,
                   MIN(next_fetch) FILTER (WHERE next_fetch > @now) AS next_fetch
            FROM feeds WHERE is_subscribed = 1
            """, new { now }),
        posts = db.QuerySingle<PostStats>("""
            SELECT COUNT(*) AS total, COUNT(*) FILTER (WHERE is_read = 0) AS unread,
                   COUNT(*) FILTER (WHERE is_starred = 1) AS starred,
                   COUNT(*) FILTER (WHERE fetched_at > @day) AS last_day,
                   (SELECT COUNT(*) FROM enclosures) AS attachments
            FROM items
            """, new { day = now - 86400 }),
        images = ImageCache.Stats(db),
        attachments = Attachments.Stats(db),
        recent = Activity.Recent,
    };
});

api.MapGet("/feeds/{id:long}/icon", (long id, HttpResponse res) =>
{
    SafeMedia(res);
    using var db = Db.Open();
    var icon = db.QuerySingleOrDefault<IconRow>("SELECT icon, icon_type FROM feeds WHERE id = @id", new { id });
    return icon?.Icon is null
        ? Results.NotFound()
        : Results.File(icon.Icon, icon.IconType ?? "image/x-icon", lastModified: null, entityTag: null);
});

api.MapPost("/folders", (FolderBody body) =>
{
    var name = body.Name.Trim();
    if (name.Length == 0) return Results.BadRequest(new { error = "Folder name is empty" });
    using var db = Db.Open();
    db.Execute("INSERT OR IGNORE INTO folders (name) VALUES (@name)", new { name });
    return Results.Ok(new { id = db.ExecuteScalar<long>("SELECT id FROM folders WHERE name = @name", new { name }), name });
});

api.MapPatch("/folders/{id:long}", (long id, FolderBody body) =>
{
    using var db = Db.Open();
    db.Execute("UPDATE folders SET name = @name WHERE id = @id", new { id, name = body.Name.Trim() });
    return Results.NoContent();
});

api.MapDelete("/folders/{id:long}", (long id) =>
{
    using var db = Db.Open();
    db.Execute("DELETE FROM folders WHERE id = @id", new { id });
    return Results.NoContent();
});

// Drag-and-drop order: the full list of what is in the top level (folderId null) or in one folder
api.MapPost("/order", (OrderBody body) =>
{
    using var db = Db.Open();
    using var tx = db.BeginTransaction();
    for (var sort = 0; sort < body.Items.Count; sort++)
    {
        var item = body.Items[sort];
        var sql = (item.Type, body.FolderId) switch
        {
            ("folder", null) => "UPDATE folders SET sort = @sort WHERE id = @id",
            ("feed", null) => "UPDATE feeds SET sort = @sort WHERE id = @id",
            ("feed", _) => "UPDATE feed_folders SET sort = @sort WHERE feed_id = @id AND folder_id = @folderId",
            _ => null,
        };
        if (sql is null) continue;
        var p = new DynamicParameters();
        p.Add("sort", sort);
        p.Add("id", item.Id);
        p.Add("folderId", body.FolderId);
        db.Execute(sql, p, tx);
    }
    tx.Commit();
    return Results.NoContent();
});

// Starts checking every feed (or every feed in one folder) in the background and returns at once;
// /api/status shows the progress
api.MapPost("/check-all", (long? folder) =>
{
    using var db = Db.Open();
    var feeds = db.Execute($"""
        UPDATE feeds SET next_fetch = 0 WHERE is_subscribed = 1
        {(folder is null ? "" : "AND id IN (SELECT feed_id FROM feed_folders WHERE folder_id = @folder)")}
        """, new { folder });
    Activity.Log("pass", $"Checking {feeds} feed{(feeds == 1 ? "" : "s")} now");
    FetchService.Kick();
    return Results.Accepted(value: new { feeds });
});

api.MapPost("/refresh", async (long? feed) =>
{
    var result = feed is long id ? await Fetcher.FetchOne(id) : await Fetcher.FetchDue(force: true);
    return Results.Ok(result);
});

api.MapPost("/opml", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    try
    {
        var count = Opml.Import(await reader.ReadToEndAsync());
        Activity.Log("subscribe", $"Imported {count} subscriptions from OPML");
        FetchService.Kick();
        return Results.Ok(new { imported = count });
    }
    catch (XmlException ex)
    {
        return Results.BadRequest(new { error = "Not a valid OPML file: " + ex.Message });
    }
});

api.MapPost("/starred", async (HttpRequest req) =>
{
    try
    {
        var count = Takeout.ImportStarred(await JsonDocument.ParseAsync(req.Body));
        Activity.Log("subscribe", $"Imported {count} starred items");
        return Results.Ok(new { imported = count });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Not a valid starred.json file: " + ex.Message });
    }
});

api.MapGet("/starred", () => Results.File(JsonSerializer.SerializeToUtf8Bytes(Takeout.ExportStarred()), "application/json", "starred.json"));

api.MapGet("/opml", () => Results.File(Encoding.UTF8.GetBytes(Opml.Export()), "text/x-opml", "subscriptions.opml"));

app.MapFallbackToFile("index.html");

// Files downloaded from feeds are served from Reader's own address, so an SVG (or anything else) opened
// in its own tab must not be able to run scripts here: no scripts, no sniffing a different type
static void SafeMedia(HttpResponse res)
{
    res.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; media-src 'self'; sandbox";
    res.Headers.XContentTypeOptions = "nosniff";
}
Console.WriteLine($"Reader Reborn {AppInfo.Version} running on {listen} (db: {dbPath})");
await app.RunAsync();

// The release version; also in web/package.json and CHANGELOG.md
static class AppInfo
{
    public const string Version = "1.0.0";
}

// ---------------------------------------------------------------------------------------------
// Request bodies and rows

record ItemPatch(bool? Read, bool? Starred);
record MarkRead(string? Stream, long? MaxId, int? OlderThanDays);
record Subscribe(string Url, long? FolderId);
record FeedPatch(string? Title, long? AddFolder, long? RemoveFolder);
record FolderBody(string Name);
record SettingsBody(bool? SaveStarredAttachments);
record BulkBody(List<long> Ids, string Action, long? FolderId);
record OrderItem(string Type, long Id);
record OrderBody(long? FolderId, List<OrderItem> Items);
record FetchResult(int Feeds, int NewItems, string? Error = null);

class FolderRow { public long Id { get; set; } public string Name { get; set; } = ""; public long Sort { get; set; } }
class IconRow { public byte[]? Icon { get; set; } public string? IconType { get; set; } }

class FeedRow
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string? SiteUrl { get; set; }
    public long Sort { get; set; }
    public List<long> FolderIds { get; set; } = [];
    public Dictionary<long, long> FolderSort { get; set; } = [];
    public long? LastFetched { get; set; }
    public string? LastError { get; set; }
    public bool HasIcon { get; set; }
    public long Unread { get; set; }
}

class ItemRow
{
    public long Id { get; set; }
    public long FeedId { get; set; }
    public string FeedTitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Author { get; set; }
    public string? Content { get; set; }
    public string? Summary { get; set; }
    public long Published { get; set; }
    public bool IsRead { get; set; }
    public bool IsStarred { get; set; }
    public List<EnclosureRow> Enclosures { get; set; } = [];
}

class EnclosureRow
{
    public string? Hash { get; set; } // a saved copy in reader-media.db (attachments of starred posts)
    public long ItemId { get; set; }
    public string Url { get; set; } = "";
    public string? Type { get; set; }
    public long? Length { get; set; }
}

class ImageRow
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string? File { get; set; }
    public string? Type { get; set; }
    public string? Hash { get; set; }
}
class BlobRow { public byte[] Data { get; set; } = []; public string Type { get; set; } = ""; }

class ImageStats
{
    public long Downloaded { get; set; }
    public long Bytes { get; set; }
    public long Pending { get; set; }
    public long Failed { get; set; }
    public long FilesToMove { get; set; }
    public long PostsToScan { get; set; }
    public bool Enabled { get; set; }
}

class TrendRow { public long Id { get; set; } public string Title { get; set; } = ""; public long Items { get; set; } public long? LastItem { get; set; } }
class FeedStats
{
    public long Subscribed { get; set; }
    public long Failing { get; set; }
    public long Retired { get; set; }
    public long Due { get; set; }
    public long? NextFetch { get; set; }
}
class PostStats { public long Total { get; set; } public long Unread { get; set; } public long Starred { get; set; } public long LastDay { get; set; } public long Attachments { get; set; } }

class StarredRow : ItemRow { public string Guid { get; set; } = ""; }

class FeedState
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SiteUrl { get; set; }
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public long ErrorCount { get; set; }
    public bool IconChecked { get; set; }
    public int? Ttl { get; set; }
}

// ---------------------------------------------------------------------------------------------
// Database

static class Db
{
    static string connectionString = "";

    public static void Init(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        using var db = Open();
        var hadEnclosures = db.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE name = 'enclosures')");
        db.Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS folders (
                id   INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS feeds (
                id            INTEGER PRIMARY KEY,
                url           TEXT NOT NULL UNIQUE,
                title         TEXT,
                custom_title  TEXT,
                site_url      TEXT,
                etag          TEXT,
                last_modified TEXT,
                last_fetched  INTEGER,
                next_fetch    INTEGER NOT NULL DEFAULT 0,
                error_count   INTEGER NOT NULL DEFAULT 0,
                last_error    TEXT,
                icon          BLOB,
                icon_type     TEXT,
                icon_checked  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS items (
                id         INTEGER PRIMARY KEY,
                feed_id    INTEGER NOT NULL REFERENCES feeds(id) ON DELETE CASCADE,
                guid       TEXT NOT NULL,
                title      TEXT NOT NULL,
                url        TEXT,
                author     TEXT,
                content    TEXT,
                summary    TEXT,
                published  INTEGER NOT NULL,
                fetched_at INTEGER NOT NULL,
                is_read    INTEGER NOT NULL DEFAULT 0,
                is_starred INTEGER NOT NULL DEFAULT 0,
                UNIQUE (feed_id, guid)
            );
            CREATE TABLE IF NOT EXISTS feed_folders (
                feed_id   INTEGER NOT NULL REFERENCES feeds(id) ON DELETE CASCADE,
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                PRIMARY KEY (feed_id, folder_id)
            );
            CREATE TABLE IF NOT EXISTS enclosures (
                item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                url     TEXT NOT NULL,
                type    TEXT,
                length  INTEGER,
                PRIMARY KEY (item_id, url)
            );
            CREATE TABLE IF NOT EXISTS images (
                id       INTEGER PRIMARY KEY,
                url      TEXT NOT NULL UNIQUE,
                file     TEXT,              -- only for images saved as files by earlier versions
                hash     TEXT,              -- the copy in reader-media.db, once downloaded
                type     TEXT,
                size     INTEGER,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_try INTEGER NOT NULL DEFAULT 0,
                error    TEXT
            );
            CREATE TABLE IF NOT EXISTS item_images (
                item_id  INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                PRIMARY KEY (item_id, image_id)
            );
            CREATE INDEX IF NOT EXISTS item_images_by_image ON item_images (image_id);
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS items_by_date ON items (published, id);
            CREATE INDEX IF NOT EXISTS items_by_feed ON items (feed_id, is_read);
            CREATE INDEX IF NOT EXISTS items_starred ON items (is_starred) WHERE is_starred = 1;
            """);

        // Re-read every feed in full once, so posts fetched before attachments were kept get theirs
        if (!hadEnclosures) db.Execute("UPDATE feeds SET etag = NULL, last_modified = NULL, next_fetch = 0");

        // Columns added after the first version
        AddColumn(db, "folders", "sort", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "feeds", "sort", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "feed_folders", "sort", "INTEGER NOT NULL DEFAULT 0");
        // 0 for feeds kept only for their starred items (imported from Takeout, or unsubscribed)
        AddColumn(db, "feeds", "is_subscribed", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(db, "feeds", "ttl", "INTEGER");          // minutes, from the feed's <ttl>
        AddColumn(db, "feeds", "check_every", "INTEGER");  // seconds between checks, as last worked out
        AddColumn(db, "feeds", "retired_at", "INTEGER");   // when the site said the feed is gone for good (410)
        AddColumn(db, "items", "images_scanned", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "images", "hash", "TEXT");
        AddColumn(db, "enclosures", "hash", "TEXT");
        AddColumn(db, "enclosures", "attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "enclosures", "next_try", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "enclosures", "error", "TEXT");

        // Databases from before a feed could be in several folders kept one folder_id per feed
        if (db.Query<string>("SELECT name FROM pragma_table_info('feeds')").Contains("folder_id"))
            db.Execute("""
                INSERT OR IGNORE INTO feed_folders (feed_id, folder_id) SELECT id, folder_id FROM feeds WHERE folder_id IS NOT NULL;
                UPDATE feeds SET folder_id = NULL;
                """);
    }

    static void AddColumn(SqliteConnection db, string table, string column, string definition)
    {
        if (!db.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Contains(column))
            db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    public static SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        db.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return db;
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

static class Subscriptions
{
    // Starred items are kept, like Reader did; their feed stays behind them, unsubscribed
    public static void Unsubscribe(SqliteConnection db, long id, SqliteTransaction? tx = null)
    {
        if (db.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM items WHERE feed_id = @id AND is_starred = 1)", new { id }, tx))
            db.Execute("""
                DELETE FROM items WHERE feed_id = @id AND is_starred = 0;
                DELETE FROM feed_folders WHERE feed_id = @id;
                UPDATE feeds SET is_subscribed = 0 WHERE id = @id;
                """, new { id }, tx);
        else
            db.Execute("DELETE FROM feeds WHERE id = @id", new { id }, tx);
    }
}

static class Streams
{
    // Stream ids: "all", "starred", "feed:<id>", "folder:<id>"
    public static (List<string> Where, DynamicParameters Params) Filter(string? stream, string? q)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        var parts = (stream ?? "all").Split(':', 2);
        switch (parts[0])
        {
            case "starred":
                where.Add("i.is_starred = 1");
                break;
            case "all":
                where.Add("f.is_subscribed = 1");
                break;
            case "feed" when parts.Length == 2 && long.TryParse(parts[1], out var feedId):
                where.Add("i.feed_id = @feedId");
                p.Add("feedId", feedId);
                break;
            case "folder" when parts.Length == 2 && long.TryParse(parts[1], out var folderId):
                where.Add("i.feed_id IN (SELECT feed_id FROM feed_folders WHERE folder_id = @folderId)");
                p.Add("folderId", folderId);
                break;
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var n = 0;
            foreach (var term in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                where.Add($"(i.title LIKE @q{n} OR i.content LIKE @q{n} OR i.author LIKE @q{n})");
                p.Add($"q{n++}", "%" + term + "%");
            }
        }
        return (where, p);
    }
}

// ---------------------------------------------------------------------------------------------
// Downloader

sealed class FetchService(ILogger<FetchService> log) : BackgroundService
{
    static readonly SemaphoreSlim kick = new(0);

    public static void Kick() => kick.Release();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var r = await Fetcher.FetchDue(force: false);
                if (r.Feeds > 0) log.LogInformation("Fetched {Feeds} feeds, {NewItems} new items", r.Feeds, r.NewItems);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Fetch pass failed");
            }
            try { await kick.WaitAsync(TimeSpan.FromMinutes(1), stop); }
            catch (OperationCanceledException) { }
        }
    }
}

// What the server has been doing, for the status dialog: the feed pass in progress, the last one, and recent events
static class Activity
{
    public static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    static readonly ConcurrentQueue<ActivityEntry> recent = new();
    static long passStarted;
    static int passTotal;
    static int passDone;

    public static void Log(string kind, string text)
    {
        recent.Enqueue(new ActivityEntry(Db.Now, kind, text));
        while (recent.Count > 200) recent.TryDequeue(out _);
    }

    public static IEnumerable<ActivityEntry> Recent => recent.Reverse().ToList();

    public static void StartPass(int feeds)
    {
        passDone = 0;
        passTotal = feeds;
        passStarted = Db.Now;
    }

    public static void FeedDone() => Interlocked.Increment(ref passDone);

    public static void EndPass(int feeds, int newItems, int errors)
    {
        LastPass = new PassSummary(passStarted, Db.Now, feeds, newItems, errors);
        passStarted = 0;
        Log("pass", $"Checked {feeds} feed{(feeds == 1 ? "" : "s")}: {newItems} new items" + (errors > 0 ? $", {errors} failed" : ""));
    }

    public static object? CurrentPass => passStarted == 0 ? null : new { started = passStarted, total = passTotal, done = passDone };

    public static PassSummary? LastPass { get; private set; }
}

record ActivityEntry(long Time, string Kind, string Text);
record PassSummary(long Started, long Finished, int Feeds, int NewItems, int Errors);

// The second database: image data, kept apart so reader.db stays small and quick to back up
static class Media
{
    static string connectionString = "";

    public static string Path { get; private set; } = "";

    public static void Init(string path)
    {
        Path = path;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        // auto_vacuum only takes effect on a new database, before the first table
        db.Execute("""
            PRAGMA auto_vacuum = INCREMENTAL;
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS blobs (
                hash    TEXT PRIMARY KEY, -- SHA-256 of data: the same file is stored once, whichever posts use it
                type    TEXT NOT NULL,    -- MIME type
                size    INTEGER NOT NULL,
                created INTEGER NOT NULL,
                data    BLOB NOT NULL
            );
            """);
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // Stores data unless it's already there; returns its hash
    public static string Put(byte[] data, string type, SqliteConnection? db = null, SqliteTransaction? tx = null)
    {
        var hash = Hash(data);
        using var own = db is null ? Open() : null;
        (db ?? own!).Execute("""
            INSERT OR IGNORE INTO blobs (hash, type, size, created, data) VALUES (@hash, @type, @size, @now, @data)
            """, new { hash, type, size = data.Length, now = Db.Now, data }, tx);
        return hash;
    }

    public static BlobRow? Get(string hash)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<BlobRow>("SELECT data, type FROM blobs WHERE hash = @hash", new { hash });
    }

    // Stores a file too big to hold in memory, streaming it into the blob; returns its hash
    public static string PutFile(string path, string type)
    {
        string hash;
        using (var file = File.OpenRead(path))
            hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        using var db = Open();
        using var tx = db.BeginTransaction();
        var size = new FileInfo(path).Length;
        if (db.Execute("""
            INSERT OR IGNORE INTO blobs (hash, type, size, created, data) VALUES (@hash, @type, @size, @now, zeroblob(@size))
            """, new { hash, type, size, now = Db.Now }, tx) == 1)
        {
            var rowid = db.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            using var blob = new SqliteBlob(db, "blobs", "data", rowid);
            using var file = File.OpenRead(path);
            file.CopyTo(blob);
        }
        tx.Commit();
        return hash;
    }

    // A read-only stream over stored data, which keeps its connection open until it's disposed
    public static Stream? OpenRead(string hash, out string type)
    {
        type = "";
        var db = Open();
        var row = db.QuerySingleOrDefault<(long RowId, string Type)?>("SELECT rowid, type FROM blobs WHERE hash = @hash", new { hash });
        if (row is null)
        {
            db.Dispose();
            return null;
        }
        type = row.Value.Type;
        return new OwnedStream(new SqliteBlob(db, "blobs", "data", row.Value.RowId, readOnly: true), db);
    }

    // Removes every stored file nothing in reader.db refers to (after unsubscribing or unstarring)
    public static int DeleteAllUnused()
    {
        HashSet<string> used;
        using (var reader = Db.Open())
            used = reader.Query<string>("SELECT hash FROM images WHERE hash IS NOT NULL UNION SELECT hash FROM enclosures WHERE hash IS NOT NULL").ToHashSet();
        using var db = Open();
        var unused = db.Query<string>("SELECT hash FROM blobs").Where(h => !used.Contains(h)).ToList();
        if (unused.Count == 0) return 0;
        db.Execute("DELETE FROM blobs WHERE hash IN @unused", new { unused });
        db.Execute("PRAGMA incremental_vacuum");
        return unused.Count;
    }

    // Removes stored data that reader.db no longer refers to
    public static void DeleteUnused(IEnumerable<string> candidates)
    {
        var hashes = candidates.Distinct().ToList();
        if (hashes.Count == 0) return;
        using (var reader = Db.Open())
        {
            var used = reader.Query<string>("SELECT hash FROM images WHERE hash IN @hashes UNION SELECT hash FROM enclosures WHERE hash IN @hashes", new { hashes }).ToHashSet();
            hashes.RemoveAll(used.Contains);
        }
        if (hashes.Count == 0) return;
        using var db = Open();
        db.Execute("DELETE FROM blobs WHERE hash IN @hashes", new { hashes });
        db.Execute("PRAGMA incremental_vacuum");
    }

    public static SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        db.Execute("PRAGMA busy_timeout = 5000;");
        return db;
    }
}

sealed class RetryLaterException(TimeSpan wait, string message) : Exception(message)
{
    public TimeSpan Wait { get; } = wait;
}

sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}

// Preferences kept on the server, because the server acts on them
static class Settings
{
    static string? Get(string key)
    {
        using var db = Db.Open();
        return db.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    static void Set(string key, string value)
    {
        using var db = Db.Open();
        db.Execute("INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", new { key, value });
    }

    // Off by default: podcast episodes are tens of megabytes each
    public static bool SaveStarredAttachments
    {
        get => Get("save_starred_attachments") == "1";
        set => Set("save_starred_attachments", value ? "1" : "0");
    }
}

// Downloads the attachments of starred posts (when that's turned on), one at a time, and drops the copies of
// posts that are no longer starred
sealed class AttachmentService(ILogger<AttachmentService> log) : BackgroundService
{
    static readonly SemaphoreSlim kick = new(0);

    public static void Kick() => kick.Release();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var lastSweep = 0L;
        while (!stop.IsCancellationRequested)
        {
            var busy = false;
            try
            {
                Attachments.ForgetUnstarred();
                if (Settings.SaveStarredAttachments) busy = await Attachments.DownloadNext(stop);
                if (Db.Now - lastSweep > 600)
                {
                    lastSweep = Db.Now;
                    if (Media.DeleteAllUnused() is var removed and > 0) log.LogInformation("Removed {Count} unused saved files", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Attachment pass failed");
            }
            try { await kick.WaitAsync(busy ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30), stop); }
            catch (OperationCanceledException) { }
        }
    }
}

static class Attachments
{
    // SQLite can't hold a single value over about 1 GB
    const long MaxBytes = 900L * 1024 * 1024;
    static readonly HttpClient http = Fetcher.CreateClient(TimeSpan.FromMinutes(30));
    static string tempFile = "";

    public static void Init(string dataDir) => tempFile = Path.Combine(dataDir, "reader-download.tmp");

    // What's being downloaded right now, for the status dialog
    public static object? Current { get; private set; }

    public static object Stats(SqliteConnection db) => new
    {
        enabled = Settings.SaveStarredAttachments,
        saved = db.ExecuteScalar<long>("SELECT COUNT(*) FROM enclosures WHERE hash IS NOT NULL"),
        bytes = db.ExecuteScalar<long>("SELECT COALESCE(SUM(length), 0) FROM (SELECT DISTINCT hash, length FROM enclosures WHERE hash IS NOT NULL)"),
        pending = db.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM enclosures e JOIN items i ON i.id = e.item_id
            WHERE i.is_starred = 1 AND e.hash IS NULL AND e.attempts < 3 AND COALESCE(e.type, '') NOT LIKE 'image/%'
            """),
        current = Current,
    };

    public static void ForgetUnstarred()
    {
        using var db = Db.Open();
        var hashes = db.Query<string>("""
            SELECT e.hash FROM enclosures e JOIN items i ON i.id = e.item_id WHERE e.hash IS NOT NULL AND i.is_starred = 0
            """).ToList();
        if (hashes.Count == 0) return;
        db.Execute("""
            UPDATE enclosures SET hash = NULL, attempts = 0, next_try = 0
            WHERE hash IS NOT NULL AND item_id IN (SELECT id FROM items WHERE is_starred = 0)
            """);
        Media.DeleteUnused(hashes);
    }

    // Saves the next attachment of a starred post; false when there's nothing to do
    public static async Task<bool> DownloadNext(CancellationToken stop)
    {
        using var db = Db.Open();
        var next = db.QuerySingleOrDefault<(long ItemId, string Url, string? Type, string Feed)?>("""
            SELECT e.item_id, e.url, e.type, COALESCE(f.custom_title, f.title, f.url) AS feed
            FROM enclosures e JOIN items i ON i.id = e.item_id JOIN feeds f ON f.id = i.feed_id
            WHERE i.is_starred = 1 AND e.hash IS NULL AND e.attempts < 3 AND e.next_try <= @now
              AND COALESCE(e.type, '') NOT LIKE 'image/%'
            ORDER BY i.id DESC LIMIT 1
            """, new { now = Db.Now });
        if (next is not { } e) return false;
        var name = Uri.TryCreate(e.Url, UriKind.Absolute, out var u) ? Path.GetFileName(u.LocalPath) : e.Url;
        try
        {
            Current = new { name, feed = e.Feed, bytes = 0L, total = (long?)null };
            using var res = await http.GetAsync(e.Url, HttpCompletionOption.ResponseHeadersRead, stop);
            Fetcher.ThrowIfAskedToWait(res);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength;
            if (total > MaxBytes) throw new InvalidDataException("Larger than 900 MB");
            await using (var body = await res.Content.ReadAsStreamAsync(stop))
            await using (var file = File.Create(tempFile))
            {
                var chunk = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await body.ReadAsync(chunk, stop)) > 0)
                {
                    done += read;
                    if (done > MaxBytes) throw new InvalidDataException("Larger than 900 MB");
                    await file.WriteAsync(chunk.AsMemory(0, read), stop);
                    Current = new { name, feed = e.Feed, bytes = done, total };
                }
            }
            // a generic Content-Type says less than the type the feed gave
            var type = res.Content.Headers.ContentType?.MediaType is { } t && t != "application/octet-stream" ? t : e.Type ?? "application/octet-stream";
            var size = new FileInfo(tempFile).Length;
            var hash = Media.PutFile(tempFile, type);
            db.Execute("UPDATE enclosures SET hash = @hash, length = @size, error = NULL WHERE item_id = @ItemId AND url = @Url",
                new { hash, size, e.ItemId, e.Url });
            Activity.Log("attachments", $"Saved {name} ({size / 1048576.0:0.0} MB) from {e.Feed}");
        }
        catch (RetryLaterException ex)
        {
            db.Execute("UPDATE enclosures SET next_try = @next, error = @error WHERE item_id = @ItemId AND url = @Url",
                new { e.ItemId, e.Url, next = Db.Now + (long)ex.Wait.TotalSeconds, error = ex.Message });
            Activity.Log("attachments", $"Waiting to save {name} from {e.Feed}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            db.Execute("""
                UPDATE enclosures SET attempts = attempts + 1, next_try = @now + 21600 * (attempts + 1) * (attempts + 1), error = @error
                WHERE item_id = @ItemId AND url = @Url
                """, new { e.ItemId, e.Url, now = Db.Now, error = ex.Message });
            Activity.Log("error", $"Couldn't save {name} from {e.Feed}: {ex.Message}");
        }
        finally
        {
            Current = null;
            File.Delete(tempFile);
        }
        return true;
    }
}

// Downloads post images in the background, separately from feeds so a big backlog doesn't hold up fetching
sealed class ImageService(ILogger<ImageService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var busy = false;
            try
            {
                var r = await ImageCache.RunOnce(stop);
                busy = r.Busy;
                if (r.Downloaded + r.Failed > 0) log.LogInformation("Images: {Downloaded} saved, {Failed} failed", r.Downloaded, r.Failed);
                if (r.Downloaded + r.Failed > 0)
                    Activity.Log("images", $"Saved {r.Downloaded} images" + (r.Failed > 0 ? $", {r.Failed} couldn't be downloaded" : ""));
                if (r.Moved > 0) Activity.Log("images", $"Moved {r.Moved} image files into reader-media.db");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Image pass failed");
            }
            try { await Task.Delay(busy ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30), stop); }
            catch (OperationCanceledException) { }
        }
    }
}

static class ImageCache
{
    const int MaxBytes = 10 * 1024 * 1024;
    static readonly Regex imgTag = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex srcAttr = new(@"\bsrc\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex srcsetAttrs = new(@"\s(?:srcset|sizes)\s*=\s*([""']).*?\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static string folder = "";

    public static bool Enabled { get; private set; }

    public static void Init(string legacyFolder, bool enabled)
    {
        folder = legacyFolder;
        Enabled = enabled;
    }

    public static string FullPath(string file) => Path.Combine(folder, file);

    public static ImageStats Stats(SqliteConnection db) => db.QuerySingle<ImageStats>("""
        SELECT COUNT(*) FILTER (WHERE hash IS NOT NULL OR file IS NOT NULL) AS downloaded,
               (SELECT COALESCE(SUM(size), 0) FROM (SELECT DISTINCT hash, size FROM images WHERE hash IS NOT NULL)) AS bytes,
               COUNT(*) FILTER (WHERE hash IS NULL AND file IS NULL AND attempts < 3) AS pending,
               COUNT(*) FILTER (WHERE hash IS NULL AND file IS NULL AND attempts >= 3) AS failed,
               COUNT(file) AS files_to_move,
               (SELECT COUNT(*) FROM items WHERE images_scanned = 0) AS posts_to_scan,
               @enabled AS enabled
        FROM images
        """, new { enabled = Enabled });

    // Before images were stored by content hash, reader-media.db kept one row per images.id with no type.
    // Converts that table once, at startup, a batch at a time.
    public static void UpgradeStorage()
    {
        using var media = Media.Open();
        if (!media.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE name = 'images')")) return;
        using var db = Db.Open();
        var total = media.ExecuteScalar<long>("SELECT COUNT(*) FROM images");
        var done = 0L;
        Console.WriteLine($"Converting {total} saved images to the new storage...");
        while (true)
        {
            var batch = media.Query<(long Id, byte[] Data)>("SELECT id, data FROM images ORDER BY id LIMIT 500").ToList();
            if (batch.Count == 0) break;
            var types = db.Query<(long Id, string? Type)>("SELECT id, type FROM images WHERE id IN @ids", new { ids = batch.Select(b => b.Id) })
                .ToDictionary(t => t.Id, t => t.Type);
            using (var mediaTx = media.BeginTransaction())
            using (var tx = db.BeginTransaction())
            {
                foreach (var (id, data) in batch)
                {
                    var type = SniffImage(data) ?? types.GetValueOrDefault(id) ?? "application/octet-stream";
                    var hash = Media.Put(data, type, media, mediaTx);
                    db.Execute("UPDATE images SET hash = @hash, type = @type WHERE id = @id", new { id, hash, type }, tx);
                }
                media.Execute("DELETE FROM images WHERE id IN @ids", new { ids = batch.Select(b => b.Id) }, mediaTx);
                tx.Commit();
                mediaTx.Commit();
            }
            done += batch.Count;
            Console.WriteLine($"  {done} of {total}");
        }
        media.Execute("DROP TABLE images; PRAGMA incremental_vacuum;");
        if (db.Query<string>("SELECT name FROM pragma_table_info('images')").Contains("saved"))
            db.Execute("ALTER TABLE images DROP COLUMN saved");
        Console.WriteLine("Done converting saved images");
    }

    // One round of background work: find new images, download some, tidy up
    public static async Task<(bool Busy, int Downloaded, int Failed, int Moved)> RunOnce(CancellationToken stop)
    {
        // moving files is quick, so it runs on its own until done instead of waiting on slow downloads
        if (MoveFiles() is var moved and > 0) return (true, 0, 0, moved);
        // copies of images nothing uses any more are removed even with downloading turned off
        if (!Enabled)
        {
            RemoveUnused();
            return (false, 0, 0, 0);
        }
        var scanned = Scan();
        var (downloaded, failed) = await DownloadPending(stop);
        RemoveUnused();
        return (scanned + downloaded + failed > 0, downloaded, failed, 0);
    }

    // Images saved as files by earlier versions move into reader-media.db, a batch at a time;
    // the images folder is removed once it's empty
    static int MoveFiles()
    {
        if (!Directory.Exists(folder)) return 0;
        using var db = Db.Open();
        var files = db.Query<ImageRow>("SELECT id, url, file, type FROM images WHERE file IS NOT NULL LIMIT 200").ToList();
        if (files.Count == 0)
        {
            Directory.Delete(folder, recursive: true);
            return 0;
        }
        using var media = Media.Open();
        using var mediaTx = media.BeginTransaction();
        using var tx = db.BeginTransaction();
        foreach (var image in files)
        {
            var path = FullPath(image.File!);
            // a missing file is downloaded again
            var data = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var hash = data is null ? null : Media.Put(data, SniffImage(data) ?? image.Type ?? "application/octet-stream", media, mediaTx);
            db.Execute("UPDATE images SET hash = @hash, file = NULL WHERE id = @Id", new { image.Id, hash }, tx);
        }
        mediaTx.Commit();
        tx.Commit();
        foreach (var image in files) File.Delete(FullPath(image.File!));
        return files.Count;
    }

    static IEnumerable<string> ImageUrls(string? html) =>
        html is null ? [] : imgTag.Matches(html)
            .Select(t => srcAttr.Match(t.Value))
            .Where(m => m.Success)
            .Select(m => WebUtility.HtmlDecode(m.Groups[2].Value).Trim())
            .Where(u => u.StartsWith("http://") || u.StartsWith("https://"));

    // Finds the images in posts that haven't been looked at yet, newest first
    static int Scan()
    {
        using var db = Db.Open();
        var items = db.Query<(long Id, string? Content)>(
            "SELECT id, content FROM items WHERE images_scanned = 0 ORDER BY id DESC LIMIT 500").ToList();
        if (items.Count == 0) return 0;
        var imageEnclosures = db.Query<(long ItemId, string Url)>(
            "SELECT item_id, url FROM enclosures WHERE type LIKE 'image/%' AND item_id IN @ids", new { ids = items.Select(i => i.Id) })
            .ToLookup(e => e.ItemId, e => e.Url);
        using var tx = db.BeginTransaction();
        foreach (var item in items)
        {
            foreach (var url in ImageUrls(item.Content).Concat(imageEnclosures[item.Id]).Distinct())
            {
                db.Execute("INSERT OR IGNORE INTO images (url) VALUES (@url)", new { url }, tx);
                db.Execute("INSERT OR IGNORE INTO item_images (item_id, image_id) SELECT @Id, id FROM images WHERE url = @url",
                    new { item.Id, url }, tx);
            }
            db.Execute("UPDATE items SET images_scanned = 1 WHERE id = @Id", new { item.Id }, tx);
        }
        tx.Commit();
        return items.Count;
    }

    static async Task<(int Downloaded, int Failed)> DownloadPending(CancellationToken stop)
    {
        List<ImageRow> pending;
        using (var db = Db.Open())
            pending = db.Query<ImageRow>("""
                SELECT id, url FROM images WHERE hash IS NULL AND file IS NULL AND attempts < 3 AND next_try <= @now ORDER BY id DESC LIMIT 30
                """, new { now = Db.Now }).ToList();
        var downloaded = 0;
        var failed = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = stop }, async (image, ct) =>
        {
            using var db = Db.Open();
            try
            {
                var (bytes, type) = await Download(image.Url, ct);
                var hash = Media.Put(bytes, type);
                db.Execute("UPDATE images SET hash = @hash, type = @type, size = @size, error = NULL WHERE id = @Id",
                    new { image.Id, hash, type, size = bytes.Length });
                Interlocked.Increment(ref downloaded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                // When the site itself can't be reached (gone, timing out, bad certificate), every image on it
                // waits for the next try instead of each one timing out in turn
                var host = new Uri(image.Url).GetLeftPart(UriPartial.Authority) + "/";
                if (ex is RetryLaterException wait)
                {
                    // the site asked us to slow down: all its images wait that long, without using up a try
                    db.Execute("""
                        UPDATE images SET next_try = @next, error = @error
                        WHERE hash IS NULL AND file IS NULL AND substr(url, 1, length(@host)) = @host
                        """, new { host, next = Db.Now + (long)wait.Wait.TotalSeconds, error = ex.Message });
                    return;
                }
                var hostDown = ex is OperationCanceledException or HttpRequestException { StatusCode: null };
                // try again in 6 hours, then a day later; give up after the third try
                var n = db.Execute($"""
                    UPDATE images SET attempts = attempts + 1, next_try = @now + 21600 * (attempts + 1) * (attempts + 1), error = @error
                    WHERE {(hostDown ? "hash IS NULL AND file IS NULL AND next_try <= @now AND substr(url, 1, length(@host)) = @host" : "id = @Id")}
                    """, new { image.Id, host, now = Db.Now, error = ex is OperationCanceledException ? "Timed out" : ex.Message });
                Interlocked.Add(ref failed, Math.Max(n, 1));
            }
        });
        return (downloaded, failed);
    }

    static async Task<(byte[] Bytes, string Type)> Download(string url, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var res = await Fetcher.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        Fetcher.ThrowIfAskedToWait(res);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Image is larger than 10 MB");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Image is larger than 10 MB");
            buffer.Write(chunk, 0, read);
        }
        // the server's Content-Type is only a claim: what's stored is what the bytes actually are
        var bytes = buffer.ToArray();
        var type = SniffImage(bytes);
        if (type is null)
        {
            var claimed = res.Content.Headers.ContentType?.MediaType;
            throw new InvalidDataException($"Not an image ({(string.IsNullOrEmpty(claimed) ? "no type" : claimed)})");
        }
        return (bytes, type);
    }

    // Image formats browsers show, recognised by their first bytes
    public static string? SniffImage(byte[] b)
    {
        bool At(int offset, params byte[] sig) => b.Length >= offset + sig.Length && b.AsSpan(offset, sig.Length).SequenceEqual(sig);
        bool Ascii(int offset, string text) => At(offset, Encoding.ASCII.GetBytes(text));
        if (At(0, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
        if (Ascii(0, "GIF87a") || Ascii(0, "GIF89a")) return "image/gif";
        if (Ascii(0, "RIFF") && Ascii(8, "WEBP")) return "image/webp";
        if (Ascii(4, "ftypavif") || Ascii(4, "ftypavis")) return "image/avif";
        if (At(0, 0x00, 0x00, 0x01, 0x00)) return "image/x-icon";
        if (Ascii(0, "BM")) return "image/bmp";
        // SVG is text: an <svg> element near the start, possibly after an XML declaration or comments
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 2048)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        return null;
    }

    // Deletes saved images no post refers to any more (after unsubscribing)
    static void RemoveUnused()
    {
        using var db = Db.Open();
        var unused = db.Query<ImageRow>("""
            SELECT id, url, file, hash FROM images WHERE NOT EXISTS (SELECT 1 FROM item_images WHERE image_id = images.id) LIMIT 500
            """).ToList();
        if (unused.Count == 0) return;
        foreach (var image in unused)
        {
            if (image.File is not null) File.Delete(FullPath(image.File));
            db.Execute("DELETE FROM images WHERE id = @Id", new { image.Id });
        }
        // another image may have the same contents, so the data only goes if nothing else uses it
        Media.DeleteUnused(unused.Where(i => i.Hash is not null).Select(i => i.Hash!));
    }

    // Points <img> tags and image attachments at saved copies; posts in the database keep the original addresses
    public static void UseLocalCopies(SqliteConnection db, List<ItemRow> items)
    {
        if (items.Count == 0) return;
        var saved = db.Query<(long ItemId, long ImageId, string Url)>("""
            SELECT ii.item_id, im.id, im.url FROM item_images ii JOIN images im ON im.id = ii.image_id
            WHERE ii.item_id IN @ids AND (im.hash IS NOT NULL OR im.file IS NOT NULL)
            """, new { ids = items.Select(i => i.Id) }).ToLookup(r => r.ItemId);
        foreach (var item in items)
        {
            var local = saved[item.Id].ToDictionary(r => r.Url, r => $"/api/images/{r.ImageId}");
            if (local.Count == 0) continue;
            if (item.Content is not null)
                item.Content = imgTag.Replace(item.Content, tag =>
                {
                    var src = srcAttr.Match(tag.Value);
                    if (!src.Success || !local.TryGetValue(WebUtility.HtmlDecode(src.Groups[2].Value).Trim(), out var path)) return tag.Value;
                    // srcset would make the browser load the original instead
                    var rewritten = tag.Value[..src.Index] + $"src=\"{path}\"" + tag.Value[(src.Index + src.Length)..];
                    return srcsetAttrs.Replace(rewritten, "");
                });
            foreach (var enclosure in item.Enclosures)
                if (local.TryGetValue(enclosure.Url, out var path)) enclosure.Url = path;
        }
    }
}

static class Fetcher
{
    static readonly SemaphoreSlim pass = new(1);
    public static readonly int IntervalMinutes = int.Parse(Environment.GetEnvironmentVariable("READER_INTERVAL") ?? "30");
    static readonly long interval = 60L * IntervalMinutes;

    static readonly HttpClient http = CreateClient();
    // feed checks follow redirects themselves, to notice when a feed has moved for good
    static readonly HttpClient feedHttp = CreateClient(followRedirects: false);
    public static HttpClient Http => http;

    // Sends the conditional request for a feed, following up to 5 redirects. When every redirect was permanent
    // (301, 308), the address it ended up at is returned so the feed's stored address can be updated;
    // temporary ones (302, 303, 307) are followed but not remembered.
    static async Task<(HttpResponseMessage Response, Uri? MovedTo)> SendFeedRequest(FeedState feed)
    {
        var url = new Uri(feed.Url);
        Uri? movedTo = null;
        var permanent = true;
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (feed.Etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", feed.Etag);
            if (feed.LastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", feed.LastModified);
            var res = await feedHttp.SendAsync(req);
            var redirect = (int)res.StatusCode is 301 or 302 or 303 or 307 or 308;
            if (!redirect || res.Headers.Location is not { } location || hop == 5)
                return (res, movedTo);
            permanent &= res.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect;
            url = new Uri(url, location);
            if (permanent) movedTo = url;
            res.Dispose();
        }
    }

    // A feed that has moved for good keeps its posts and settings under the new address
    static void MoveFeed(SqliteConnection db, FeedState feed, Uri movedTo)
    {
        var url = movedTo.AbsoluteUri;
        if (url == feed.Url) return;
        if (db.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM feeds WHERE url = @url)", new { url }))
        {
            Activity.Log("feed", $"{feed.Title} has moved to {url}, which you're already subscribed to");
            return;
        }
        db.Execute("UPDATE feeds SET url = @url WHERE id = @Id", new { url, feed.Id });
        Activity.Log("feed", $"{feed.Title} has moved to {url}; its address is updated");
    }

    // How long until a feed is checked again: the configured interval, or longer when the server or the feed
    // says it can be cached for longer (Cache-Control max-age, Expires, RSS <ttl>). Never sooner than the
    // interval, and never later than a day, so new posts still show up.
    static long NextCheckDelay(HttpResponseMessage res, int? ttlMinutes)
    {
        double hint = 0;
        if (res.Headers.CacheControl?.MaxAge is { } maxAge)
            hint = maxAge.TotalSeconds;
        else if (res.Content.Headers.Expires is { } expires)
            hint = (expires - (res.Headers.Date ?? DateTimeOffset.UtcNow)).TotalSeconds;
        if (ttlMinutes is > 0) hint = Math.Max(hint, ttlMinutes.Value * 60.0);
        return Math.Clamp((long)hint, interval, 86400);
    }

    // A site asking us to slow down: 429 Too Many Requests, or 503 with a Retry-After. A 503 without one is
    // an ordinary error. Waits are kept between a minute and a week.
    public static void ThrowIfAskedToWait(HttpResponseMessage res)
    {
        if (res.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return;
        var hint = res.Headers.RetryAfter;
        var wait = hint?.Delta ?? (hint?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is null && res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        var clamped = TimeSpan.FromSeconds(Math.Clamp((wait ?? TimeSpan.FromHours(1)).TotalSeconds, 60, 7 * 86400));
        throw new RetryLaterException(clamped, $"The site asked to wait {(clamped.TotalHours >= 1 ? $"{clamped.TotalHours:0.#} h" : $"{clamped.TotalMinutes:0} min")} ({(int)res.StatusCode})");
    }

    public static HttpClient CreateClient(TimeSpan? timeout = null, bool followRedirects = true)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // be polite: many feeds can live on one site (FeedBurner, Medium...), so extra requests queue
            MaxConnectionsPerServer = 2,
        }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (compatible; ReaderReborn/{AppInfo.Version}; +https://github.com/mika76/reader-reborn)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.8");
        return client;
    }

    public static async Task<FetchResult> FetchDue(bool force)
    {
        await pass.WaitAsync();
        try
        {
            List<long> ids;
            using (var db = Db.Open())
                ids = db.Query<long>("SELECT id FROM feeds WHERE is_subscribed = 1 AND retired_at IS NULL AND (@force OR next_fetch <= @now)", new { force, now = Db.Now }).ToList();
            if (ids.Count == 0) return new FetchResult(0, 0);
            var total = 0;
            var errors = 0;
            Activity.StartPass(ids.Count);
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, _) =>
            {
                var r = await FetchOne(id);
                Interlocked.Add(ref total, r.NewItems);
                if (r.Error is not null) Interlocked.Increment(ref errors);
                Activity.FeedDone();
            });
            Activity.EndPass(ids.Count, total, errors);
            return new FetchResult(ids.Count, total);
        }
        finally
        {
            pass.Release();
        }
    }

    public static async Task<FetchResult> FetchOne(long id)
    {
        using var db = Db.Open();
        var feed = db.QuerySingleOrDefault<FeedState>(
            "SELECT id, url, COALESCE(custom_title, title, url) AS title, site_url, etag, last_modified, error_count, icon_checked, ttl FROM feeds WHERE id = @id", new { id });
        if (feed is null) return new FetchResult(0, 0, "No such feed");
        try
        {
            var (res, movedTo) = await SendFeedRequest(feed);
            using var _ = res;
            ThrowIfAskedToWait(res);
            if (res.StatusCode == HttpStatusCode.Gone)
            {
                // the site says the feed is gone for good: stop checking it (refreshing it by hand still tries)
                const string retired = "The site says this feed has been removed for good (410 Gone), so it's no longer checked";
                db.Execute("UPDATE feeds SET last_fetched = @now, retired_at = @now, last_error = @retired WHERE id = @id",
                    new { id, now = Db.Now, retired });
                Activity.Log("error", $"{feed.Title}: removed by its site (410 Gone); it won't be checked any more");
                return new FetchResult(1, 0, retired);
            }
            var added = 0;
            if (res.StatusCode != HttpStatusCode.NotModified)
            {
                res.EnsureSuccessStatusCode();
                var bytes = await res.Content.ReadAsByteArrayAsync();
                var parsed = FeedParser.Parse(bytes, res.RequestMessage?.RequestUri ?? new Uri(feed.Url))
                    ?? throw new InvalidDataException("Not a valid RSS or Atom feed");
                added = Store(db, id, parsed);
                feed.SiteUrl = parsed.SiteUrl ?? feed.SiteUrl;
                feed.Ttl = parsed.TtlMinutes;
                db.Execute("""
                    UPDATE feeds SET title = COALESCE(NULLIF(@title, ''), title), site_url = COALESCE(@siteUrl, site_url),
                                     etag = @etag, last_modified = @lastModified, ttl = @ttl WHERE id = @id
                    """, new
                {
                    id,
                    ttl = parsed.TtlMinutes,
                    title = parsed.Title,
                    siteUrl = parsed.SiteUrl,
                    etag = res.Headers.ETag?.ToString(),
                    lastModified = res.Content.Headers.LastModified?.ToString("R"),
                });
            }
            if (movedTo is not null) MoveFeed(db, feed, movedTo);
            var delay = NextCheckDelay(res, feed.Ttl);
            db.Execute("""
                UPDATE feeds SET last_fetched = @now, next_fetch = @next, check_every = @delay, error_count = 0, last_error = NULL, retired_at = NULL
                WHERE id = @id
                """, new { id, now = Db.Now, next = Db.Now + delay, delay });
            if (!feed.IconChecked) await FetchIcon(db, feed);
            if (added > 0) Activity.Log("feed", $"{feed.Title}: {added} new item{(added == 1 ? "" : "s")}");
            return new FetchResult(1, added);
        }
        catch (RetryLaterException ex)
        {
            // the site is fine, just busy: wait as asked, without counting it as a failure
            db.Execute("UPDATE feeds SET last_fetched = @now, next_fetch = @next, last_error = @error WHERE id = @id",
                new { id, now = Db.Now, next = Db.Now + Math.Max((long)ex.Wait.TotalSeconds, interval), error = ex.Message });
            Activity.Log("feed", $"{feed.Title}: {ex.Message}");
            return new FetchResult(1, 0, ex.Message);
        }
        catch (Exception ex)
        {
            var backoff = Math.Min(interval << (int)Math.Min(feed.ErrorCount + 1, 6), 86400);
            db.Execute("""
                UPDATE feeds SET last_fetched = @now, next_fetch = @next, error_count = error_count + 1, last_error = @error WHERE id = @id
                """, new { id, now = Db.Now, next = Db.Now + backoff, error = ex.Message });
            Activity.Log("error", $"{feed.Title}: {ex.Message}");
            return new FetchResult(1, 0, ex.Message);
        }
    }

    static int Store(SqliteConnection db, long feedId, ParsedFeed feed)
    {
        using var tx = db.BeginTransaction();
        var now = Db.Now;
        var added = 0;
        foreach (var item in feed.Items)
        {
            var published = Math.Min(item.Published?.ToUnixTimeSeconds() ?? now, now);
            added += db.Execute("""
                INSERT OR IGNORE INTO items (feed_id, guid, title, url, author, content, summary, published, fetched_at)
                VALUES (@feedId, @Guid, @Title, @Url, @Author, @Content, @Summary, @published, @now)
                """, new { feedId, item.Guid, item.Title, item.Url, item.Author, item.Content, item.Summary, published, now }, tx);
            if (item.Enclosures.Count == 0) continue;
            var itemId = db.ExecuteScalar<long>("SELECT id FROM items WHERE feed_id = @feedId AND guid = @Guid", new { feedId, item.Guid }, tx);
            foreach (var enclosure in item.Enclosures)
                db.Execute("INSERT OR IGNORE INTO enclosures (item_id, url, type, length) VALUES (@itemId, @Url, @Type, @Length)",
                    new { itemId, enclosure.Url, enclosure.Type, enclosure.Length }, tx);
        }
        tx.Commit();
        return added;
    }

    public static async Task<FeedRow> Subscribe(string input, long? folderId)
    {
        input = input.Trim();
        if (!input.Contains("://")) input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("That doesn't look like a web address");

        var url = await Discover(uri);
        using var db = Db.Open();
        db.Execute("INSERT INTO feeds (url) VALUES (@url) ON CONFLICT (url) DO UPDATE SET is_subscribed = 1", new { url });
        var id = db.ExecuteScalar<long>("SELECT id FROM feeds WHERE url = @url", new { url });
        if (folderId is not null)
            db.Execute("INSERT OR IGNORE INTO feed_folders (feed_id, folder_id) VALUES (@id, @folderId)", new { id, folderId });
        var result = await FetchOne(id);
        if (result.Error is not null) throw new InvalidDataException(result.Error);
        return db.QuerySingle<FeedRow>("SELECT id, COALESCE(custom_title, title, url) AS title, url, site_url FROM feeds WHERE id = @id", new { id });
    }

    // Accepts a feed URL or a web page URL; for pages, follows <link rel="alternate"> or common feed paths.
    static async Task<string> Discover(Uri uri)
    {
        var (bytes, final) = await Get(uri);
        if (FeedParser.Parse(bytes, final) is not null) return final.AbsoluteUri;

        var html = Encoding.UTF8.GetString(bytes);
        var candidates = Html.Links(html, final)
            .Where(l => l.Rel.Contains("alternate") && Regex.IsMatch(l.Type, @"(rss|atom|rdf)\+xml|application/xml|text/xml"))
            .Select(l => l.Href)
            .Concat(new[] { "/feed", "/rss", "/feed.xml", "/atom.xml", "/rss.xml", "/index.xml", "/feed/" }
                .Select(p => new Uri(final, p)))
            .Distinct();
        foreach (var candidate in candidates)
        {
            try
            {
                var (b, f) = await Get(candidate);
                if (FeedParser.Parse(b, f) is not null) return f.AbsoluteUri;
            }
            catch (HttpRequestException) { }
        }
        throw new InvalidDataException("No feed found at that address");
    }

    static async Task<(byte[] Bytes, Uri Final)> Get(Uri uri)
    {
        using var res = await http.GetAsync(uri);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadAsByteArrayAsync(), res.RequestMessage?.RequestUri ?? uri);
    }

    static async Task FetchIcon(SqliteConnection db, FeedState feed)
    {
        db.Execute("UPDATE feeds SET icon_checked = 1 WHERE id = @Id", feed);
        var site = new Uri(feed.SiteUrl ?? feed.Url);
        var candidates = new List<Uri>();
        try
        {
            var (bytes, final) = await Get(new Uri(site.GetLeftPart(UriPartial.Authority)));
            candidates.AddRange(Html.Links(Encoding.UTF8.GetString(bytes), final)
                .Where(l => l.Rel.Split(' ').Contains("icon"))
                .OrderBy(l => l.Type.Contains("svg") ? 1 : 0)
                .Select(l => l.Href));
        }
        catch (Exception) { }
        candidates.Add(new Uri(site, "/favicon.ico"));

        foreach (var candidate in candidates.Distinct().Take(4))
        {
            try
            {
                using var res = await http.GetAsync(candidate);
                if (!res.IsSuccessStatusCode) continue;
                var bytes = await res.Content.ReadAsByteArrayAsync();
                var type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (bytes.Length == 0 || bytes.Length > 512 * 1024 || !(type.StartsWith("image/") || LooksLikeImage(bytes))) continue;
                db.Execute("UPDATE feeds SET icon = @bytes, icon_type = @type WHERE id = @id",
                    new { id = feed.Id, bytes, type = type.StartsWith("image/") ? type : "image/x-icon" });
                return;
            }
            catch (Exception) { }
        }
    }

    static bool LooksLikeImage(byte[] b) =>
        b.Length > 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 1) || (b[0] == 0x89 && b[1] == 'P') || (b[0] == 'G' && b[1] == 'I'));
}

// ---------------------------------------------------------------------------------------------
// Feed parsing (RSS 0.9x/2.0, RSS 1.0/RDF, Atom 1.0), namespace-agnostic and lenient

record ParsedFeed(string Title, string? SiteUrl, List<ParsedItem> Items, int? TtlMinutes = null);
record ParsedItem(string Guid, string Title, string? Url, string? Author, string Content, string Summary, DateTimeOffset? Published, List<Enclosure> Enclosures);
record Enclosure(string Url, string? Type, long? Length);

static class FeedParser
{
    public static ParsedFeed? Parse(byte[] bytes, Uri baseUri)
    {
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if (start >= bytes.Length || bytes[start] != '<') return null;

        XDocument doc;
        try
        {
            using var stream = new MemoryStream(bytes, start, bytes.Length - start);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                CheckCharacters = false,
                XmlResolver = null,
            });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root!;
        return root.Name.LocalName switch
        {
            "feed" => ParseAtom(root, baseUri),
            "rss" => Child(root, "channel") is { } channel ? ParseRss(channel, channel.Elements().Where(e => e.Name.LocalName == "item"), baseUri) : null,
            "RDF" => Child(root, "channel") is { } channel ? ParseRss(channel, root.Elements().Where(e => e.Name.LocalName == "item"), baseUri) : null,
            _ => null,
        };
    }

    static ParsedFeed ParseRss(XElement channel, IEnumerable<XElement> entries, Uri baseUri)
    {
        var site = RssLink(channel) is { } s ? Resolve(baseUri, s) : null;
        var items = new List<ParsedItem>();
        foreach (var e in entries)
        {
            var link = RssLink(e) is { } l ? Resolve(baseUri, l) : null;
            var content = Text(e, "encoded") ?? Text(e, "description") ?? Media(e) ?? "";
            var title = PlainTitle(Text(e, "title"));
            var published = Dates.Parse(Text(e, "pubDate") ?? Text(e, "date") ?? Text(e, "published") ?? Text(e, "updated"));
            var guid = Text(e, "guid") ?? link ?? Hash(title + published + content);
            items.Add(Item(guid, title, link, Text(e, "creator") ?? Text(e, "author"), content, published, link ?? site, baseUri, Enclosures(e, baseUri)));
        }
        // RSS <ttl>: how many minutes the feed may be cached before it's worth checking again
        int? ttl = int.TryParse(Text(channel, "ttl"), out var minutes) && minutes > 0 ? minutes : null;
        return new ParsedFeed(PlainTitle(Text(channel, "title")), site, items, ttl);
    }

    static ParsedFeed ParseAtom(XElement feed, Uri baseUri)
    {
        baseUri = XmlBase(feed, baseUri);
        var site = AtomLink(feed) is { } s ? Resolve(baseUri, s) : null;
        var feedAuthor = Text(Child(feed, "author"), "name");
        var items = new List<ParsedItem>();
        foreach (var e in feed.Elements().Where(e => e.Name.LocalName == "entry"))
        {
            var entryBase = XmlBase(e, baseUri);
            var link = AtomLink(e) is { } l ? Resolve(entryBase, l) : null;
            var content = AtomContent(Child(e, "content")) ?? AtomContent(Child(e, "summary")) ?? Media(e) ?? "";
            var title = PlainTitle(Text(e, "title"));
            var published = Dates.Parse(Text(e, "published") ?? Text(e, "updated") ?? Text(e, "issued") ?? Text(e, "modified"));
            var guid = Text(e, "id") ?? link ?? Hash(title + published + content);
            items.Add(Item(guid, title, link, Text(Child(e, "author"), "name") ?? feedAuthor, content, published, link ?? site, entryBase, Enclosures(e, entryBase)));
        }
        return new ParsedFeed(PlainTitle(Text(feed, "title")), site, items);
    }

    static ParsedItem Item(string guid, string title, string? link, string? author, string content, DateTimeOffset? published, string? contentBase, Uri fallbackBase, List<Enclosure> enclosures)
    {
        var baseUri = contentBase is not null && Uri.TryCreate(contentBase, UriKind.Absolute, out var b) ? b : fallbackBase;
        content = Html.AbsolutizeUrls(content, baseUri);
        var summary = Html.ToText(content);
        if (title.Length == 0) title = summary.Length > 80 ? summary[..80] + "…" : summary.Length > 0 ? summary : "(title unknown)";
        return new ParsedItem(guid.Trim(), title, link, author?.Trim(), content, summary.Length > 400 ? summary[..400] : summary, published, enclosures);
    }

    // Attachments: RSS <enclosure>, Atom <link rel="enclosure">, and audio/video in Media RSS
    static List<Enclosure> Enclosures(XElement e, Uri baseUri)
    {
        var found = new List<Enclosure>();
        void Add(string? url, string? type, string? length)
        {
            if (string.IsNullOrWhiteSpace(url) || Resolve(baseUri, url) is not { } abs || found.Any(f => f.Url == abs)) return;
            found.Add(new Enclosure(abs, string.IsNullOrWhiteSpace(type) ? null : type.Trim(), long.TryParse(length, out var n) && n > 0 ? n : null));
        }
        foreach (var c in e.Elements())
        {
            var name = c.Name.LocalName;
            if (name == "enclosure")
                Add((string?)c.Attribute("url"), (string?)c.Attribute("type"), (string?)c.Attribute("length"));
            else if (name == "link" && (string?)c.Attribute("rel") == "enclosure")
                Add((string?)c.Attribute("href"), (string?)c.Attribute("type"), (string?)c.Attribute("length"));
        }
        foreach (var m in e.Descendants().Where(d => d.Name.LocalName == "content" && d.Attribute("url") is not null))
        {
            var type = (string?)m.Attribute("type") ?? "";
            var medium = (string?)m.Attribute("medium") ?? "";
            if (type.StartsWith("audio/") || type.StartsWith("video/") || medium is "audio" or "video")
                Add((string?)m.Attribute("url"), type.Length > 0 ? type : medium + "/*", (string?)m.Attribute("fileSize"));
        }
        return found;
    }

    static XElement? Child(XElement? e, string local) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    static string? Text(XElement? e, string local)
    {
        var v = Child(e, local)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    static string? RssLink(XElement e)
    {
        var links = e.Elements().Where(c => c.Name.LocalName == "link").ToList();
        return links.Select(l => l.Value.Trim()).FirstOrDefault(v => v.Length > 0)
            ?? links.Where(l => (string?)l.Attribute("rel") is null or "alternate").Select(l => (string?)l.Attribute("href")).FirstOrDefault(h => !string.IsNullOrEmpty(h));
    }

    static string? AtomLink(XElement e)
    {
        var links = e.Elements().Where(c => c.Name.LocalName == "link").ToList();
        return (links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault(l => (string?)l.Attribute("rel") != "self"))
            ?.Attribute("href")?.Value;
    }

    static string? AtomContent(XElement? e)
    {
        if (e is null) return null;
        var type = (string?)e.Attribute("type") ?? "text";
        var value = type switch
        {
            "xhtml" => string.Concat(e.Elements().FirstOrDefault()?.Nodes() ?? e.Nodes()),
            "html" or "text/html" => e.Value,
            _ when type.Contains("xml") => string.Concat(e.Nodes()),
            _ => WebUtility.HtmlEncode(e.Value).Replace("\n", "<br>"),
        };
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // YouTube and podcast feeds put the description and thumbnail in Media RSS
    static string? Media(XElement e)
    {
        var thumb = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "thumbnail")?.Attribute("url")?.Value;
        var desc = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "description")?.Value;
        if (thumb is null && desc is null) return null;
        var sb = new StringBuilder();
        if (thumb is not null) sb.Append($"<p><img src=\"{WebUtility.HtmlEncode(thumb)}\"></p>");
        if (desc is not null) sb.Append("<p>").Append(WebUtility.HtmlEncode(desc).Replace("\n", "<br>")).Append("</p>");
        return sb.ToString();
    }

    static Uri XmlBase(XElement e, Uri baseUri) =>
        e.Attribute(XNamespace.Xml + "base")?.Value is { } b && Uri.TryCreate(baseUri, b, out var u) ? u : baseUri;

    static string? Resolve(Uri baseUri, string url) =>
        Uri.TryCreate(baseUri, url.Trim(), out var u) ? u.AbsoluteUri : null;

    static string PlainTitle(string? title) =>
        title is null ? "" : Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(title, "<[^>]+>", "")), @"\s+", " ").Trim();

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}

static class Dates
{
    static readonly string[] months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static readonly Dictionary<string, int> zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0, ["EST"] = -300, ["EDT"] = -240, ["CST"] = -360, ["CDT"] = -300,
        ["MST"] = -420, ["MDT"] = -360, ["PST"] = -480, ["PDT"] = -420, ["CET"] = 60, ["CEST"] = 120, ["BST"] = 60,
        ["EET"] = 120, ["EEST"] = 180, ["JST"] = 540, ["AEST"] = 600, ["AEDT"] = 660,
    };
    static readonly Regex rfc822 = new(@"^(?:[A-Za-z]+,?\s*)?(\d{1,2})\s+([A-Za-z]{3})[A-Za-z]*\.?\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(.*)$");

    public static DateTimeOffset? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var m = rfc822.Match(s);
        if (m.Success)
        {
            try
            {
                var month = Array.IndexOf(months, m.Groups[2].Value.ToLowerInvariant()) + 1;
                var year = int.Parse(m.Groups[3].Value);
                if (year < 100) year += year < 70 ? 2000 : 1900;
                var zone = m.Groups[7].Value.Trim();
                var offset = zones.TryGetValue(zone, out var mins) ? TimeSpan.FromMinutes(mins)
                    : Regex.Match(zone, @"^([+-])(\d{2}):?(\d{2})") is { Success: true } z
                        ? TimeSpan.FromMinutes((z.Groups[1].Value == "-" ? -1 : 1) * (int.Parse(z.Groups[2].Value) * 60 + int.Parse(z.Groups[3].Value)))
                        : TimeSpan.Zero;
                if (month > 0)
                    return new DateTimeOffset(year, month, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value),
                        int.Parse(m.Groups[5].Value), m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0, offset);
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}

record HtmlLink(string Rel, string Type, Uri Href);

static class Html
{
    static readonly Regex linkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex attr = new(@"([\w-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))");
    static readonly Regex urlAttr = new(@"(\b(?:src|href|poster)\s*=\s*)([""'])(.*?)\2", RegexOptions.IgnoreCase);

    public static IEnumerable<HtmlLink> Links(string html, Uri baseUri)
    {
        foreach (Match tag in linkTag.Matches(html))
        {
            var a = new Dictionary<string, string>();
            foreach (Match m in attr.Matches(tag.Value))
                a.TryAdd(m.Groups[1].Value.ToLowerInvariant(),
                    WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value));
            if (a.TryGetValue("href", out var href) && Uri.TryCreate(baseUri, href, out var uri))
                yield return new HtmlLink(a.GetValueOrDefault("rel", "").ToLowerInvariant(), a.GetValueOrDefault("type", "").ToLowerInvariant(), uri);
        }
    }

    public static string AbsolutizeUrls(string html, Uri baseUri) => urlAttr.Replace(html, m =>
    {
        var value = WebUtility.HtmlDecode(m.Groups[3].Value);
        if (value.StartsWith('#') || value.Contains(':') && Uri.IsWellFormedUriString(value, UriKind.Absolute)) return m.Value;
        return Uri.TryCreate(baseUri, value, out var abs) && abs.Scheme is "http" or "https"
            ? m.Groups[1].Value + m.Groups[2].Value + abs.AbsoluteUri.Replace("\"", "%22") + m.Groups[2].Value
            : m.Value;
    });

    public static string ToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
    }
}

static class Opml
{
    public static int Import(string xml)
    {
        var doc = XDocument.Parse(xml);
        var body = doc.Root?.Element("body") ?? throw new XmlException("Missing <body>");
        using var db = Db.Open();
        using var tx = db.BeginTransaction();
        var count = 0;
        void Walk(XElement parent, long? folderId)
        {
            foreach (var o in parent.Elements("outline"))
            {
                var url = (string?)o.Attribute("xmlUrl");
                var name = ((string?)o.Attribute("title") ?? (string?)o.Attribute("text"))?.Trim();
                if (url is not null)
                {
                    count += db.Execute("""
                        INSERT INTO feeds (url, title, site_url) VALUES (@url, @name, @site)
                        ON CONFLICT (url) DO UPDATE SET is_subscribed = 1, next_fetch = 0 WHERE is_subscribed = 0
                        """,
                        new { url, name, site = (string?)o.Attribute("htmlUrl") }, tx);
                    if (folderId is not null)
                        db.Execute("""
                            INSERT OR IGNORE INTO feed_folders (feed_id, folder_id) SELECT id, @folderId FROM feeds WHERE url = @url
                            """, new { url, folderId }, tx);
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    db.Execute("INSERT OR IGNORE INTO folders (name) VALUES (@name)", new { name }, tx);
                    Walk(o, db.ExecuteScalar<long>("SELECT id FROM folders WHERE name = @name", new { name }, tx));
                }
                else
                {
                    Walk(o, folderId);
                }
            }
        }
        Walk(body, null);
        tx.Commit();
        return count;
    }

    public static string Export()
    {
        using var db = Db.Open();
        var folders = db.Query<FolderRow>("SELECT id, name FROM folders ORDER BY name COLLATE NOCASE").ToList();
        var feeds = db.Query<FeedRow>("SELECT id, COALESCE(custom_title, title, url) AS title, url, site_url FROM feeds WHERE is_subscribed = 1 ORDER BY title COLLATE NOCASE").ToList();
        var memberships = db.Query<(long FeedId, long FolderId)>("SELECT feed_id, folder_id FROM feed_folders").ToList();
        XElement Outline(FeedRow f) => new("outline", new XAttribute("text", f.Title), new XAttribute("title", f.Title),
            new XAttribute("type", "rss"), new XAttribute("xmlUrl", f.Url), f.SiteUrl is null ? null : new XAttribute("htmlUrl", f.SiteUrl));
        var body = new XElement("body",
            folders.Select(folder => new XElement("outline", new XAttribute("text", folder.Name), new XAttribute("title", folder.Name),
                feeds.Where(f => memberships.Contains((f.Id, folder.Id))).Select(Outline))),
            feeds.Where(f => !memberships.Any(m => m.FeedId == f.Id)).Select(Outline));
        var doc = new XDocument(new XElement("opml", new XAttribute("version", "1.0"),
            new XElement("head", new XElement("title", "Reader Reborn subscriptions")), body));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + doc;
    }
}

// Google Takeout's Reader export: starred.json (and shared.json, liked.json) in the Reader API format
static class Takeout
{
    public static int ImportStarred(JsonDocument doc)
    {
        using var db = Db.Open();
        using var tx = db.BeginTransaction();
        var count = 0;
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing \"items\"");
        foreach (var item in items.EnumerateArray())
        {
            var origin = Get(item, "origin");
            var stream = Str(origin, "streamId") ?? "";
            var feedUrl = stream.StartsWith("feed/") ? stream[5..] : "takeout:" + (Str(origin, "htmlUrl") ?? "unknown");
            db.Execute("""
                INSERT INTO feeds (url, title, site_url, is_subscribed) VALUES (@feedUrl, @title, @site, 0)
                ON CONFLICT (url) DO NOTHING
                """, new { feedUrl, title = Str(origin, "title"), site = Str(origin, "htmlUrl") }, tx);
            var feedId = db.ExecuteScalar<long>("SELECT id FROM feeds WHERE url = @feedUrl", new { feedUrl }, tx);

            var url = Href(item, "alternate") ?? Href(item, "canonical");
            var content = Str(Get(item, "content"), "content") ?? Str(Get(item, "summary"), "content") ?? "";
            var guid = Str(item, "id") ?? url ?? Guid.NewGuid().ToString();
            var published = Get(item, "published")?.TryGetInt64(out var t) == true ? t : Db.Now;

            // the same story may already be here from fetching the feed, under the feed's own id
            var existing = url is null ? null : db.ExecuteScalar<long?>(
                "SELECT id FROM items WHERE feed_id = @feedId AND url = @url", new { feedId, url }, tx);
            if (existing is not null)
            {
                count += db.Execute("UPDATE items SET is_starred = 1 WHERE id = @existing AND is_starred = 0", new { existing }, tx);
                AddEnclosures(db, tx, existing.Value, item);
                continue;
            }
            var title = Str(item, "title") is { } raw ? WebUtility.HtmlDecode(raw) : "(title unknown)";
            var summary = Html.ToText(content);
            count += db.Execute("""
                INSERT INTO items (feed_id, guid, title, url, author, content, summary, published, fetched_at, is_read, is_starred)
                VALUES (@feedId, @guid, @title, @url, @author, @content, @summary, @published, @now, 1, 1)
                ON CONFLICT (feed_id, guid) DO UPDATE SET is_starred = 1 WHERE is_starred = 0
                """, new
            {
                feedId, guid, title, url, author = Str(item, "author"), content,
                summary = summary.Length > 400 ? summary[..400] : summary, published, now = Db.Now,
            }, tx);
            AddEnclosures(db, tx, db.ExecuteScalar<long>("SELECT id FROM items WHERE feed_id = @feedId AND guid = @guid", new { feedId, guid }, tx), item);
        }
        tx.Commit();
        return count;
    }

    static void AddEnclosures(SqliteConnection db, SqliteTransaction tx, long itemId, JsonElement item)
    {
        if (Get(item, "enclosure") is not { ValueKind: JsonValueKind.Array } enclosures) return;
        foreach (var e in enclosures.EnumerateArray())
        {
            if (Str(e, "href") is not { } url) continue;
            var length = Str(e, "length") is { } l && long.TryParse(l, out var n) && n > 0 ? n : (long?)null;
            db.Execute("INSERT OR IGNORE INTO enclosures (item_id, url, type, length) VALUES (@itemId, @url, @type, @length)",
                new { itemId, url, type = Str(e, "type"), length }, tx);
        }
    }

    public static object ExportStarred()
    {
        using var db = Db.Open();
        var items = db.Query<StarredRow>("""
            SELECT i.id, i.feed_id, COALESCE(f.custom_title, f.title, f.url) AS feed_title, i.title, i.url, i.author,
                   i.content, i.published, i.guid
            FROM items i JOIN feeds f ON f.id = i.feed_id WHERE i.is_starred = 1 ORDER BY i.published DESC
            """).ToList();
        var feeds = db.Query<FeedRow>("SELECT id, url, site_url FROM feeds").ToDictionary(f => f.Id);
        var enclosures = db.Query<EnclosureRow>("""
            SELECT e.item_id, e.url, e.type, e.length FROM enclosures e JOIN items i ON i.id = e.item_id WHERE i.is_starred = 1
            """).ToLookup(e => e.ItemId);
        return new
        {
            id = "user/-/state/com.google/starred",
            title = "Starred items",
            author = "",
            updated = Db.Now,
            direction = "ltr",
            items = items.Select(i => new
            {
                id = i.Guid,
                title = i.Title,
                published = i.Published,
                updated = i.Published,
                timestampUsec = (i.Published * 1_000_000).ToString(),
                alternate = i.Url is null ? [] : new[] { new { href = i.Url, type = "text/html" } },
                content = new { direction = "ltr", content = i.Content ?? "" },
                enclosure = enclosures[i.Id].Select(e => new { href = e.Url, type = e.Type, length = e.Length?.ToString() }),
                author = i.Author,
                origin = new
                {
                    streamId = "feed/" + feeds[i.FeedId].Url,
                    title = i.FeedTitle,
                    htmlUrl = feeds[i.FeedId].SiteUrl,
                },
            }),
        };
    }

    static JsonElement? Get(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    static string? Str(JsonElement? e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    static string? Href(JsonElement item, string name) =>
        Get(item, name) is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? Str(a[0], "href") : null;
}
