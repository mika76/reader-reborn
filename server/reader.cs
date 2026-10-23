#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Data.Sqlite@10.0.12
#:package Dapper@2.1.89
#:property PublishAot=false
#:property InvariantGlobalization=true

// Reader backend: a single-file .NET app that keeps subscriptions in SQLite, downloads
// feeds on a schedule and serves the JSON API plus the built Vue frontend (web/dist).
//
//   ./reader.cs                  serve on http://localhost:5199 and fetch feeds in the background
//   ./reader.cs fetch            fetch every feed once and exit (cron-friendly)
//   ./reader.cs add <url>        subscribe to a feed or a site (autodiscovery)
//   ./reader.cs import <x.opml>  import subscriptions from an OPML file
//
// Environment: READER_DB (sqlite path, default data/reader.db), READER_PORT (default 5199),
// READER_INTERVAL (minutes between fetches of one feed, default 30), READER_IMAGES (set to "off" to
// stop downloading post images; they are kept in a second database, reader-media.db, next to the first).

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

switch (args.FirstOrDefault())
{
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
    WebRootPath = Path.Combine(root, "web", "dist"),
});
builder.WebHost.UseUrls($"http://localhost:{Environment.GetEnvironmentVariable("READER_PORT") ?? "5199"}");
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddHostedService<FetchService>();
builder.Services.AddHostedService<ImageService>();
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
    var enclosures = db.Query<EnclosureRow>("SELECT item_id, url, type, length FROM enclosures WHERE item_id IN @ids",
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
    // keep starred items, like Reader did; the feed stays behind them, unsubscribed
    if (db.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM items WHERE feed_id = @id AND is_starred = 1)", new { id }))
        db.Execute("""
            DELETE FROM items WHERE feed_id = @id AND is_starred = 0;
            DELETE FROM feed_folders WHERE feed_id = @id;
            UPDATE feeds SET is_subscribed = 0 WHERE id = @id;
            """, new { id });
    else
        db.Execute("DELETE FROM feeds WHERE id = @id", new { id });
    return Results.NoContent();
});

api.MapGet("/images/{id:long}", (long id, HttpResponse res) =>
{
    using var db = Db.Open();
    var image = db.QuerySingleOrDefault<ImageRow>("SELECT id, url, file, type, saved FROM images WHERE id = @id", new { id });
    var data = image is { Saved: true } ? ImageCache.Load(id) : null;
    if (data is null && image?.File is null) return Results.NotFound();
    res.Headers.CacheControl = "public, max-age=31536000, immutable";
    var type = image!.Type ?? "application/octet-stream";
    // files from before images were kept in reader-media.db, until they are moved in
    return data is not null ? Results.Bytes(data, type) : Results.File(ImageCache.FullPath(image.File!), type);
});

api.MapGet("/images/stats", () =>
{
    using var db = Db.Open();
    return db.QuerySingle<ImageStats>("""
        SELECT COUNT(*) FILTER (WHERE saved = 1 OR file IS NOT NULL) AS downloaded,
               COALESCE(SUM(size) FILTER (WHERE saved = 1 OR file IS NOT NULL), 0) AS bytes,
               COUNT(*) FILTER (WHERE saved = 0 AND file IS NULL AND attempts < 3) AS pending,
               COUNT(*) FILTER (WHERE saved = 0 AND file IS NULL AND attempts >= 3) AS failed,
               @enabled AS enabled
        FROM images
        """, new { enabled = ImageCache.Enabled });
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
                   COUNT(*) FILTER (WHERE last_error IS NOT NULL) AS failing,
                   COUNT(*) FILTER (WHERE next_fetch <= @now) AS due,
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
        images = db.QuerySingle<ImageStats>("""
            SELECT COUNT(*) FILTER (WHERE saved = 1 OR file IS NOT NULL) AS downloaded,
                   COALESCE(SUM(size) FILTER (WHERE saved = 1 OR file IS NOT NULL), 0) AS bytes,
                   COUNT(*) FILTER (WHERE saved = 0 AND file IS NULL AND attempts < 3) AS pending,
                   COUNT(*) FILTER (WHERE saved = 0 AND file IS NULL AND attempts >= 3) AS failed,
                   COUNT(file) AS files_to_move,
                   (SELECT COUNT(*) FROM items WHERE images_scanned = 0) AS posts_to_scan,
                   @enabled AS enabled
            FROM images
            """, new { enabled = ImageCache.Enabled }),
        recent = Activity.Recent,
    };
});

api.MapGet("/feeds/{id:long}/icon", (long id) =>
{
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
Console.WriteLine($"Reader running on {string.Join(", ", app.Urls.DefaultIfEmpty($"http://localhost:{Environment.GetEnvironmentVariable("READER_PORT") ?? "5199"}"))} (db: {dbPath})");
await app.RunAsync();

// ---------------------------------------------------------------------------------------------
// Request bodies and rows

record ItemPatch(bool? Read, bool? Starred);
record MarkRead(string? Stream, long? MaxId, int? OlderThanDays);
record Subscribe(string Url, long? FolderId);
record FeedPatch(string? Title, long? AddFolder, long? RemoveFolder);
record FolderBody(string Name);
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
    public bool Saved { get; set; }
}
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

class FeedStats { public long Subscribed { get; set; } public long Failing { get; set; } public long Due { get; set; } public long? NextFetch { get; set; } }
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
                saved    INTEGER NOT NULL DEFAULT 0, -- 1 once the image is in reader-media.db
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
        AddColumn(db, "items", "images_scanned", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "images", "saved", "INTEGER NOT NULL DEFAULT 0");

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
            CREATE TABLE IF NOT EXISTS images (
                id   INTEGER PRIMARY KEY, -- images.id in reader.db
                data BLOB NOT NULL
            );
            """);
    }

    public static SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        db.Execute("PRAGMA busy_timeout = 5000;");
        return db;
    }
}

// Downloads post images in the background, separately from feeds so a big backlog doesn't hold up fetching
sealed class ImageService(ILogger<ImageService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!ImageCache.Enabled) return;
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

    public static byte[]? Load(long id)
    {
        using var media = Media.Open();
        return media.QuerySingleOrDefault<byte[]>("SELECT data FROM images WHERE id = @id", new { id });
    }

    // One round of background work: find new images, download some, tidy up
    public static async Task<(bool Busy, int Downloaded, int Failed, int Moved)> RunOnce(CancellationToken stop)
    {
        // moving files is quick, so it runs on its own until done instead of waiting on slow downloads
        if (MoveFiles() is var moved and > 0) return (true, 0, 0, moved);
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
        var files = db.Query<ImageRow>("SELECT id, url, file FROM images WHERE file IS NOT NULL LIMIT 200").ToList();
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
            if (File.Exists(path))
                media.Execute("INSERT OR REPLACE INTO images (id, data) VALUES (@Id, @data)", new { image.Id, data = File.ReadAllBytes(path) }, mediaTx);
            // a missing file is downloaded again
            db.Execute("UPDATE images SET saved = @saved, file = NULL WHERE id = @Id", new { image.Id, saved = File.Exists(path) }, tx);
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
                SELECT id, url FROM images WHERE saved = 0 AND file IS NULL AND attempts < 3 AND next_try <= @now ORDER BY id DESC LIMIT 30
                """, new { now = Db.Now }).ToList();
        var downloaded = 0;
        var failed = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = stop }, async (image, ct) =>
        {
            using var db = Db.Open();
            try
            {
                var (bytes, type) = await Download(image.Url, ct);
                using (var media = Media.Open())
                    media.Execute("INSERT OR REPLACE INTO images (id, data) VALUES (@Id, @bytes)", new { image.Id, bytes });
                db.Execute("UPDATE images SET saved = 1, type = @type, size = @size, error = NULL WHERE id = @Id",
                    new { image.Id, type, size = bytes.Length });
                Interlocked.Increment(ref downloaded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                // When the site itself can't be reached (gone, timing out, bad certificate), every image on it
                // waits for the next try instead of each one timing out in turn
                var hostDown = ex is OperationCanceledException or HttpRequestException { StatusCode: null };
                var host = new Uri(image.Url).GetLeftPart(UriPartial.Authority) + "/";
                // try again in 6 hours, then a day later; give up after the third try
                var n = db.Execute($"""
                    UPDATE images SET attempts = attempts + 1, next_try = @now + 21600 * (attempts + 1) * (attempts + 1), error = @error
                    WHERE {(hostDown ? "saved = 0 AND file IS NULL AND next_try <= @now AND substr(url, 1, length(@host)) = @host" : "id = @Id")}
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
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("image/")) throw new InvalidDataException($"Not an image ({(type.Length > 0 ? type : "no type")})");
        return (buffer.ToArray(), type);
    }

    // Deletes saved images no post refers to any more (after unsubscribing)
    static void RemoveUnused()
    {
        using var db = Db.Open();
        var unused = db.Query<ImageRow>("""
            SELECT id, url, file FROM images WHERE NOT EXISTS (SELECT 1 FROM item_images WHERE image_id = images.id) LIMIT 500
            """).ToList();
        if (unused.Count == 0) return;
        using var media = Media.Open();
        foreach (var image in unused)
        {
            if (image.File is not null) File.Delete(FullPath(image.File));
            media.Execute("DELETE FROM images WHERE id = @Id", new { image.Id });
            db.Execute("DELETE FROM images WHERE id = @Id", new { image.Id });
        }
        media.Execute("PRAGMA incremental_vacuum");
    }

    // Points <img> tags and image attachments at saved copies; posts in the database keep the original addresses
    public static void UseLocalCopies(SqliteConnection db, List<ItemRow> items)
    {
        if (items.Count == 0) return;
        var saved = db.Query<(long ItemId, long ImageId, string Url)>("""
            SELECT ii.item_id, im.id, im.url FROM item_images ii JOIN images im ON im.id = ii.image_id
            WHERE ii.item_id IN @ids AND (im.saved = 1 OR im.file IS NOT NULL)
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
    public static HttpClient Http => http;

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Reader/1.0; +https://github.com/)");
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
                ids = db.Query<long>("SELECT id FROM feeds WHERE is_subscribed = 1 AND (@force OR next_fetch <= @now)", new { force, now = Db.Now }).ToList();
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
            "SELECT id, url, COALESCE(custom_title, title, url) AS title, site_url, etag, last_modified, error_count, icon_checked FROM feeds WHERE id = @id", new { id });
        if (feed is null) return new FetchResult(0, 0, "No such feed");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, feed.Url);
            if (feed.Etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", feed.Etag);
            if (feed.LastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", feed.LastModified);
            using var res = await http.SendAsync(req);
            var added = 0;
            if (res.StatusCode != HttpStatusCode.NotModified)
            {
                res.EnsureSuccessStatusCode();
                var bytes = await res.Content.ReadAsByteArrayAsync();
                var parsed = FeedParser.Parse(bytes, res.RequestMessage?.RequestUri ?? new Uri(feed.Url))
                    ?? throw new InvalidDataException("Not a valid RSS or Atom feed");
                added = Store(db, id, parsed);
                feed.SiteUrl = parsed.SiteUrl ?? feed.SiteUrl;
                db.Execute("""
                    UPDATE feeds SET title = COALESCE(NULLIF(@title, ''), title), site_url = COALESCE(@siteUrl, site_url),
                                     etag = @etag, last_modified = @lastModified WHERE id = @id
                    """, new
                {
                    id,
                    title = parsed.Title,
                    siteUrl = parsed.SiteUrl,
                    etag = res.Headers.ETag?.ToString(),
                    lastModified = res.Content.Headers.LastModified?.ToString("R"),
                });
            }
            db.Execute("""
                UPDATE feeds SET last_fetched = @now, next_fetch = @next, error_count = 0, last_error = NULL WHERE id = @id
                """, new { id, now = Db.Now, next = Db.Now + interval });
            if (!feed.IconChecked) await FetchIcon(db, feed);
            if (added > 0) Activity.Log("feed", $"{feed.Title}: {added} new item{(added == 1 ? "" : "s")}");
            return new FetchResult(1, added);
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

record ParsedFeed(string Title, string? SiteUrl, List<ParsedItem> Items);
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
        return new ParsedFeed(PlainTitle(Text(channel, "title")), site, items);
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
            new XElement("head", new XElement("title", "Reader subscriptions")), body));
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
