using System.Text.Json;

namespace Server.Tests;

// Each feeds/<name>.xml is subscribed to and its posts are compared with feeds/<name>.expected.json.
// Expected fields that are left out aren't checked; "published": "now" means "no usable date", so the
// time it was fetched; "content" lists pieces the post's HTML must contain.
[Collection("reader")]
public class FeedParserTests(ReaderFixture f)
{
    public static TheoryData<string> Samples() =>
        new(Directory.GetFiles(FeedSite.FeedsDir, "*.expected.json").Select(p => Path.GetFileName(p).Replace(".expected.json", "")).Order());

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Sample_feed_is_read_as_expected(string name)
    {
        var expected = JsonDocument.Parse(f.Site.Fill(File.ReadAllText(Path.Combine(FeedSite.FeedsDir, name + ".expected.json")))).RootElement;
        var subscribed = await f.Reader.Subscribe(f.Site.Url($"/feeds/{name}.xml"));
        var id = subscribed.GetProperty("id").GetInt64();

        var feed = await f.Reader.Feed(id);
        Assert.Equal(expected.GetProperty("title").GetString(), feed.GetProperty("title").GetString());
        Assert.Equal(expected.GetProperty("siteUrl").GetString(), feed.GetProperty("siteUrl").GetString());

        var items = await f.Reader.Items("feed:" + id);
        var want = expected.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(want.Select(Title).Order(), items.Select(Title).Order());
        foreach (var w in want)
        {
            var item = items.Single(i => Title(i) == Title(w));
            var title = Title(w);
            foreach (var field in new[] { "url", "author", "summary" })
                if (w.TryGetProperty(field, out var value))
                    Assert.True(value.GetString() == item.GetProperty(field).GetString(),
                        $"{title}: {field} was {item.GetProperty(field)}, expected {value}");
            if (w.TryGetProperty("published", out var published))
            {
                var actual = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("published").GetInt64());
                if (published.GetString() == "now")
                    Assert.True(Math.Abs((DateTimeOffset.UtcNow - actual).TotalMinutes) < 10, $"{title}: published {actual:u}, expected about now");
                else
                    Assert.True(DateTimeOffset.Parse(published.GetString()!) == actual, $"{title}: published {actual:u}, expected {published}");
            }
            if (w.TryGetProperty("content", out var pieces))
            {
                var content = item.GetProperty("content").GetString() ?? "";
                foreach (var piece in pieces.EnumerateArray())
                    Assert.True(content.Contains(piece.GetString()!), $"{title}: content doesn't contain {piece}:\n{content}");
            }
            if (w.TryGetProperty("enclosures", out var enclosures))
            {
                var got = item.GetProperty("enclosures").EnumerateArray()
                    .Select(e => (e.GetProperty("url").GetString(), Str(e, "type"), Long(e, "length"))).OrderBy(e => e.Item1).ToList();
                var exp = enclosures.EnumerateArray()
                    .Select(e => (e.GetProperty("url").GetString(), Str(e, "type"), Long(e, "length"))).OrderBy(e => e.Item1).ToList();
                Assert.Equal(exp, got);
            }
        }
    }

    [Fact]
    public async Task A_web_page_is_followed_to_the_feed_it_links_to()
    {
        var feed = await f.Reader.Subscribe(f.Site.Url("/feeds/discover.html"));
        Assert.Equal(f.Site.Url("/feeds/discover-feed.xml"), feed.GetProperty("url").GetString());
        Assert.Equal("Discovered", feed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Something_that_isnt_a_feed_is_refused()
    {
        var res = await f.Reader.Send(HttpMethod.Post, "/api/feeds", new { url = f.Site.Url("/feeds/does-not-exist.xml") });
        Assert.Equal(400, (int)res.StatusCode);
    }

    static string? Title(JsonElement e) => e.GetProperty("title").GetString();
    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
