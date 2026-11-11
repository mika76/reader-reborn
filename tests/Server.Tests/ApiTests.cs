using System.Text.Json;
using System.Xml.Linq;

namespace Server.Tests;

[Collection("reader")]
public class ApiTests(ReaderFixture f)
{
    ReaderServer R => f.Reader;

    // Subscribes to a made-up feed with these post titles; returns the feed's id
    async Task<long> MakeFeed(string name, params string[] titles)
    {
        f.Site.Made[name + ".xml"] = FeedSite.Rss(name, titles);
        return (await R.Subscribe(f.Site.Url($"/made/{name}.xml"))).GetProperty("id").GetInt64();
    }

    static List<string?> Titles(IEnumerable<JsonElement> items) => items.Select(i => i.GetProperty("title").GetString()).ToList();

    [Fact]
    public async Task Subscribing_lists_the_feed_with_its_unread_count()
    {
        var id = await MakeFeed("counted", "a", "b", "c");
        var feed = await R.Feed(id);
        Assert.Equal("counted", feed.GetProperty("title").GetString());
        Assert.Equal(3, feed.GetProperty("unread").GetInt64());
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task Subscribing_to_something_that_isnt_an_address_is_refused()
    {
        var res = await R.Send(HttpMethod.Post, "/api/feeds", new { url = "ftp://nope" });
        Assert.Equal(400, (int)res.StatusCode);
    }

    [Fact]
    public async Task Items_come_in_pages_newest_or_oldest_first()
    {
        var id = await MakeFeed("paged", "p1", "p2", "p3", "p4", "p5");
        var first = await R.Get($"/api/items?stream=feed:{id}&limit=2");
        Assert.Equal(["p5", "p4"], Titles(first.GetProperty("items").EnumerateArray()));
        var c = first.GetProperty("continuation");
        var second = await R.Get($"/api/items?stream=feed:{id}&limit=2&cursorTs={c.GetProperty("ts")}&cursorId={c.GetProperty("id")}");
        Assert.Equal(["p3", "p2"], Titles(second.GetProperty("items").EnumerateArray()));

        var oldest = await R.Get($"/api/items?stream=feed:{id}&limit=10&sort=oldest");
        Assert.Equal(["p1", "p2", "p3", "p4", "p5"], Titles(oldest.GetProperty("items").EnumerateArray()));
        Assert.Equal(JsonValueKind.Null, oldest.GetProperty("continuation").ValueKind);
    }

    [Fact]
    public async Task Search_finds_posts_by_every_word()
    {
        var id = await MakeFeed("searchable", "apple pie", "apple tart", "pear pie");
        Assert.Equal(["apple pie"], Titles(await R.Items($"feed:{id}", "&q=apple%20pie")));
        Assert.Equal(["apple pie", "pear pie"], Titles(await R.Items($"feed:{id}", "&q=pie")));
    }

    [Fact]
    public async Task Reading_and_starring_change_the_counts()
    {
        var id = await MakeFeed("readable", "r1", "r2");
        var items = await R.Items($"feed:{id}");
        var r1 = items[0].GetProperty("id").GetInt64();
        var starredBefore = (await R.Get("/api/subscriptions")).GetProperty("starred").GetInt64();

        await R.Post($"/api/items/{r1}", new { read = true, starred = true });
        Assert.Equal(1, (await R.Feed(id)).GetProperty("unread").GetInt64());
        Assert.Equal(["r2"], Titles(await R.Items($"feed:{id}", "&unread=true")));
        Assert.Equal(starredBefore + 1, (await R.Get("/api/subscriptions")).GetProperty("starred").GetInt64());
        Assert.Contains("r1", Titles(await R.Items("starred")));

        await R.Post($"/api/items/{r1}", new { read = false });
        Assert.Equal(2, (await R.Feed(id)).GetProperty("unread").GetInt64());
    }

    [Fact]
    public async Task Mark_all_as_read_leaves_posts_that_arrived_since()
    {
        var id = await MakeFeed("markable", "m1", "m2");
        var maxId = (await R.Get($"/api/items?stream=feed:{id}")).GetProperty("maxId").GetInt64();
        // a post that arrives after the list was loaded
        f.Site.Made["markable.xml"] = FeedSite.Rss("markable", "m1", "m2", "m3");
        await R.Post($"/api/refresh?feed={id}");

        var r = await R.Post("/api/items/mark-read", new { stream = $"feed:{id}", maxId });
        Assert.Equal(2, r.GetProperty("marked").GetInt32());
        Assert.Equal(["m3"], Titles(await R.Items($"feed:{id}", "&unread=true")));
    }

    [Fact]
    public async Task Mark_as_read_older_than_a_number_of_days()
    {
        // the made-up feed's posts are from 2024, so a day's cutoff marks them all, and the newest post here isn't
        f.Site.Made["aged.xml"] = FeedSite.Rss("aged", "old").Replace("</channel>", "<item><title>new</title><guid>new</guid></item></channel>");
        var id = (await R.Subscribe(f.Site.Url("/made/aged.xml"))).GetProperty("id").GetInt64();
        await R.Post("/api/items/mark-read", new { stream = $"feed:{id}", olderThanDays = 1 });
        Assert.Equal(["new"], Titles(await R.Items($"feed:{id}", "&unread=true")));
    }

    [Fact]
    public async Task Folders_hold_feeds_and_deleting_one_keeps_them()
    {
        var folder = await R.Post("/api/folders", new { name = "Comics" });
        var folderId = folder.GetProperty("id").GetInt64();
        f.Site.Made["folded.xml"] = FeedSite.Rss("folded", "f1");
        var inFolder = (await R.Subscribe(f.Site.Url("/made/folded.xml"), folderId)).GetProperty("id").GetInt64();
        var other = await MakeFeed("unfolded", "u1");
        await R.Patch($"/api/feeds/{other}", new { addFolder = folderId });
        await R.Patch($"/api/folders/{folderId}", new { name = "Funnies" });

        var subs = await R.Get("/api/subscriptions");
        Assert.Contains(subs.GetProperty("folders").EnumerateArray(), x => x.GetProperty("name").GetString() == "Funnies");
        Assert.Contains(folderId, (await R.Feed(inFolder)).GetProperty("folderIds").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal(["f1", "u1"], Titles(await R.Items($"folder:{folderId}")).Order());

        // moving: out of the folder, to the top level
        await R.Patch($"/api/feeds/{other}", new { removeFolder = folderId });
        Assert.Equal(["f1"], Titles(await R.Items($"folder:{folderId}")));

        await R.Delete($"/api/folders/{folderId}");
        Assert.Empty((await R.Feed(inFolder)).GetProperty("folderIds").EnumerateArray());
    }

    [Fact]
    public async Task Renaming_a_feed_and_clearing_the_name_again()
    {
        var id = await MakeFeed("named", "n1");
        await R.Patch($"/api/feeds/{id}", new { title = "My name" });
        Assert.Equal("My name", (await R.Feed(id)).GetProperty("title").GetString());
        await R.Patch($"/api/feeds/{id}", new { title = " " });
        Assert.Equal("named", (await R.Feed(id)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Bulk_actions_file_and_unsubscribe_several_feeds()
    {
        var a = await MakeFeed("bulk-a", "x");
        var b = await MakeFeed("bulk-b", "y");
        var folderId = (await R.Post("/api/folders", new { name = "Bulk" })).GetProperty("id").GetInt64();
        await R.Post("/api/feeds/bulk", new { ids = new[] { a, b }, action = "addFolder", folderId });
        Assert.Equal(2, (await R.Items($"folder:{folderId}")).Count);
        await R.Post("/api/feeds/bulk", new { ids = new[] { a, b }, action = "unsubscribe" });
        var ids = (await R.Get("/api/subscriptions")).GetProperty("feeds").EnumerateArray().Select(x => x.GetProperty("id").GetInt64());
        Assert.DoesNotContain(a, ids);
        Assert.DoesNotContain(b, ids);

        var bad = await R.Send(HttpMethod.Post, "/api/feeds/bulk", new { ids = new[] { a }, action = "explode" });
        Assert.Equal(400, (int)bad.StatusCode);
    }

    [Fact]
    public async Task Unsubscribing_keeps_starred_posts()
    {
        var id = await MakeFeed("leaving", "keep me", "lose me");
        var keep = (await R.Items($"feed:{id}")).Single(i => i.GetProperty("title").GetString() == "keep me").GetProperty("id").GetInt64();
        await R.Post($"/api/items/{keep}", new { starred = true });
        await R.Delete($"/api/feeds/{id}");

        Assert.DoesNotContain((await R.Get("/api/subscriptions")).GetProperty("feeds").EnumerateArray(), x => x.GetProperty("id").GetInt64() == id);
        Assert.Contains("keep me", Titles(await R.Items("starred")));
        Assert.DoesNotContain("lose me", Titles(await R.Items("all")));
    }

    [Fact]
    public async Task Opml_export_imports_into_a_new_server_with_the_same_folders()
    {
        var folderId = (await R.Post("/api/folders", new { name = "Exported & Co" })).GetProperty("id").GetInt64();
        f.Site.Made["opml-in.xml"] = FeedSite.Rss("opml-in", "o1");
        await R.Subscribe(f.Site.Url("/made/opml-in.xml"), folderId);
        await MakeFeed("opml-loose", "o2");

        var opml = await R.GetText("/api/opml");
        var outlines = XDocument.Parse(opml).Descendants("outline").ToList();
        Assert.Contains(outlines, o => (string?)o.Attribute("text") == "Exported & Co"
            && o.Elements().Any(c => (string?)c.Attribute("xmlUrl") == f.Site.Url("/made/opml-in.xml")));

        await using var fresh = await ReaderServer.StartAsync();
        var imported = await fresh.Post("/api/opml", opml);
        Assert.Equal(outlines.Count(o => o.Attribute("xmlUrl") is not null), imported.GetProperty("imported").GetInt32());
        var subs = await fresh.Get("/api/subscriptions");
        var folder = subs.GetProperty("folders").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Exported & Co");
        var feed = subs.GetProperty("feeds").EnumerateArray().Single(x => x.GetProperty("url").GetString() == f.Site.Url("/made/opml-in.xml"));
        Assert.Equal([folder.GetProperty("id").GetInt64()], feed.GetProperty("folderIds").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal("opml-in", feed.GetProperty("title").GetString());

        // the same file again adds nothing
        Assert.Equal(0, (await fresh.Post("/api/opml", opml)).GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task Bad_opml_is_refused()
    {
        var res = await R.Send(HttpMethod.Post, "/api/opml", "<opml><head/>");
        Assert.Equal(400, (int)res.StatusCode);
    }

    [Fact]
    public async Task Starred_json_export_imports_into_a_new_server()
    {
        var id = await MakeFeed("starry", "s1", "s2");
        var s1 = (await R.Items($"feed:{id}")).Single(i => i.GetProperty("title").GetString() == "s1");
        await R.Post($"/api/items/{s1.GetProperty("id")}", new { starred = true });

        var json = await R.GetText("/api/starred");
        var exported = JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(exported, i => i.GetProperty("title").GetString() == "s1");

        await using var fresh = await ReaderServer.StartAsync();
        var imported = await fresh.Post("/api/starred", json);
        Assert.Equal(exported.Count, imported.GetProperty("imported").GetInt32());
        var starred = await fresh.Items("starred");
        var copy = starred.Single(i => i.GetProperty("title").GetString() == "s1");
        Assert.Equal(s1.GetProperty("url").GetString(), copy.GetProperty("url").GetString());
        Assert.Equal(s1.GetProperty("content").GetString(), copy.GetProperty("content").GetString());
        Assert.Equal(s1.GetProperty("published").GetInt64(), copy.GetProperty("published").GetInt64());
        Assert.Equal("starry", copy.GetProperty("feedTitle").GetString());
        // starred items from feeds you don't follow don't add subscriptions
        Assert.Empty((await fresh.Get("/api/subscriptions")).GetProperty("feeds").EnumerateArray());

        Assert.Equal(0, (await fresh.Post("/api/starred", json)).GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task A_feed_that_moved_for_good_gets_its_new_address()
    {
        var id = await MakeFeed("moving", "mv1");
        f.Site.Made["moved.xml"] = FeedSite.Rss("moving", "mv1", "mv2");
        f.Site.Made["moving.xml"] = "moved:moved.xml";
        await R.Post($"/api/refresh?feed={id}");
        var feed = await R.Feed(id);
        Assert.Equal(f.Site.Url("/made/moved.xml"), feed.GetProperty("url").GetString());
        Assert.Equal(2, feed.GetProperty("unread").GetInt64());
    }

    [Fact]
    public async Task A_feed_its_site_says_is_gone_stops_being_checked()
    {
        var id = await MakeFeed("going", "g1");
        f.Site.Made["going.xml"] = "gone";
        await R.Post($"/api/refresh?feed={id}");
        Assert.Contains("410", (await R.Feed(id)).GetProperty("lastError").GetString());
        var status = await R.Get("/api/status");
        Assert.True(status.GetProperty("feeds").GetProperty("retired").GetInt64() >= 1);

        // checking everything leaves it alone
        var asked = f.Site.Requests.GetValueOrDefault("going.xml");
        await R.Post("/api/refresh");
        Assert.Equal(asked, f.Site.Requests.GetValueOrDefault("going.xml"));
    }

    [Fact]
    public async Task Pictures_are_saved_and_removed_with_their_feed()
    {
        f.Site.Made["pictures.xml"] = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>pictures</title><link>{{base}}/</link>
            <item><title>With a picture</title><guid>pic-1</guid>
            <description><![CDATA[<p><img src="{{base}}/images/ok-one.png" srcset="{{base}}/images/ok-big.png 2x"></p>]]></description></item>
            </channel></rss>
            """;
        var id = (await R.Subscribe(f.Site.Url("/made/pictures.xml"))).GetProperty("id").GetInt64();
        await ReaderServer.Eventually(async () =>
            ((await R.Items($"feed:{id}")).Single().GetProperty("content").GetString() ?? "").Contains("/api/images/"), what: "the picture to be saved");

        var content = (await R.Items($"feed:{id}")).Single().GetProperty("content").GetString()!;
        Assert.DoesNotContain("srcset", content);
        var path = System.Text.RegularExpressions.Regex.Match(content, "/api/images/\\d+").Value;
        var image = await R.Http.GetAsync(path);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FeedSite.Png, await image.Content.ReadAsByteArrayAsync());

        await R.Delete($"/api/feeds/{id}");
        await ReaderServer.Eventually(async () => (await R.Http.GetAsync(path)).StatusCode == System.Net.HttpStatusCode.NotFound,
            what: "the picture to be removed");
    }

    [Fact]
    public async Task Status_reports_the_version_from_the_changelog()
    {
        var changelog = File.ReadAllText(Path.Combine(ReaderServer.RepoRoot, "CHANGELOG.md"));
        var newest = System.Text.RegularExpressions.Regex.Match(changelog, @"(?m)^## \[(\d+\.\d+\.\d+[^\]]*)\]").Groups[1].Value;
        var status = await R.Get("/api/status");
        Assert.Equal(newest, status.GetProperty("server").GetProperty("version").GetString());
    }
}
