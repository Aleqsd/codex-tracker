using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>Public discovery only. Neither the feed's classifications nor its text are evidence.</summary>
public sealed class GlobalResetReader(HttpClient http)
{
    public const string FeedUrl = "https://shixilin.com/ai/codex-claude-resets/events.json";
    private static readonly string[] Authors = ["thsottiaux", "OpenAI", "OpenAIDevs"];
    private static readonly string[] PaidPlans = ["plus", "pro", "prolite", "team", "business", "enterprise", "edu"];
    // This pair was reviewed together. oEmbed cannot prove a reply relationship, so an
    // ambiguous completion must NEVER inherit scope from an arbitrary nearby post.
    private static readonly Dictionary<string, string> ReviewedContexts = new()
    {
        ["2103911959544610829"] = "2103637477760311522"
    };
    private sealed record Post(string Id, string Author, string Url, DateTimeOffset At, string Text = "");

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<GlobalResetFeedState> ReadAsync(DateTimeOffset now, CancellationToken token = default)
    {
        using var feed = await GetJsonAsync(FeedUrl, 1_048_576, token);
        if (feed.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid public feed.");
        var candidates = feed.RootElement.EnumerateArray()
            .Select(row => ParseUrl(String(row, "sourceUrl")))
            .OfType<Post>().Where(p => p.At <= now && now - p.At < TimeSpan.FromHours(60))
            .DistinctBy(p => p.Id).OrderByDescending(p => p.At).ToArray();
        if (candidates.Length > 16) throw new InvalidDataException("Too many recent public posts.");
        var posts = new List<Post>();
        // Fail closed if any recent source is unavailable: it may contain a correction.
        foreach (var candidate in candidates) posts.Add(await VerifyAsync(candidate, token));
        var results = new List<GlobalResetAnnouncement>();
        foreach (var post in posts.Where(p => now - p.At < TimeSpan.FromHours(24)))
        {
            var text = Normalize(post.Text);
            Post? context = null;
            string[] plans;
            ResetKind[] kinds;
            if (ReviewedContexts.TryGetValue(post.Id, out var contextId))
            {
                context = posts.SingleOrDefault(p => p.Id == contextId && p.Author == post.Author);
                // Compare reviewed original wording, not a translation or feed excerpt.
                if (context is null || text != "resets all propagated. that will be all. have a fantastic weekend." ||
                    !Normalize(context.Text).Contains("we'll reset usage limits for all paid users across codex and chatgpt work", StringComparison.Ordinal) ||
                    UnsafeScope(Normalize(context.Text))) continue;
                plans = PaidPlans;
                kinds = [ResetKind.Weekly, ResetKind.Short];
            }
            else if (!TrySelfContained(text, out plans, out kinds)) continue;

            // A newer correction/qualification on the same source suspends the inference.
            if (posts.Any(p => p.Author == post.Author && p.At > post.At &&
                Match(Normalize(p.Text), @"\b(resets?|limits)\b") &&
                Match(Normalize(p.Text), @"\b(correction|incorrect|mistake|reverted|cancelled|canceled|not all|not reset|failed|didn't|haven't)\b"))) continue;
            var announcement = new GlobalResetAnnouncement(post.Id, post.Author, post.Url, context?.Url ?? post.Url,
                context?.At ?? post.At, post.At, now, plans, kinds);
            if (announcement.IsCurrent(now)) results.Add(announcement);
        }
        return new(results, now);
    }

    private static bool TrySelfContained(string text, out string[] plans, out ResetKind[] kinds)
    {
        plans = []; kinds = [];
        if (UnsafeScope(text)) return false;
        // Deliberately narrow grammar. Unknown wording stays unknown. Requiring scope and
        // product in the same sentence avoids borrowing them from a quote or another topic.
        var match = Regex.Match(text,
            @"(?:^|[.!]\s+)(?:update: )?we(?:'ve| have) (?:now |just )?reset (?:the )?(?<kind>weekly |5-hour |5-hour and weekly )?(?:usage |rate )limits (?:in |for )?codex for (?<scope>all paid users|all users across all plans)[.!]?(?:$|\s)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return false;
        plans = match.Groups["scope"].Value == "all paid users" ? PaidPlans : [.. PaidPlans, "free"];
        kinds = match.Groups["kind"].Value switch
        {
            "weekly " => [ResetKind.Weekly], "5-hour " => [ResetKind.Short], _ => [ResetKind.Weekly, ResetKind.Short]
        };
        return true;
    }
    private static bool UnsafeScope(string text) => Match(text,
        @"\b(banked|credits?|reserve|except|excluding|only|some|eligible|affected|might|maybe|hypothetical|quote|not|haven't|didn't|won't)\b|\?");
    private static string Normalize(string text) => Regex.Replace(text.Replace('’', '\'').ToLowerInvariant(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private async Task<Post> VerifyAsync(Post candidate, CancellationToken token)
    {
        using var json = await GetJsonAsync("https://publish.x.com/oembed?url=" + Uri.EscapeDataString(candidate.Url) + "&omit_script=true", 65_536, token);
        var root = json.RootElement;
        var returned = ParseUrl(String(root, "url"));
        if (returned?.Id != candidate.Id || returned.Author != candidate.Author ||
            String(root, "author_url") != "https://x.com/" + candidate.Author ||
            String(root, "type") != "rich" || String(root, "provider_url") != "https://x.com")
            throw new InvalidDataException("Public source identity mismatch.");
        var html = String(root, "html") ?? "";
        var paragraph = Regex.Match(html, @"<p\b[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!paragraph.Success || !html.Contains("https://x.com/" + candidate.Author + "/status/" + candidate.Id + "?", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported public source.");
        var body = Regex.Replace(paragraph.Groups[1].Value, @"<a\b[^>]*>.*?</a>", "", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        body = Regex.Replace(body, @"<[^>]*>", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        body = WebUtility.HtmlDecode(body);
        if (body.Length > 8_000) throw new InvalidDataException("Public text too large.");
        return candidate with { Text = body };
    }

    private static Post? ParseUrl(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, @"\Ahttps://x\.com/([A-Za-z0-9_]+)/status/([0-9]{18,20})\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !Authors.Contains(match.Groups[1].Value) || !ulong.TryParse(match.Groups[2].Value, out var id)) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(id >> 22) + 1288834974657L);
        return new(match.Groups[2].Value, match.Groups[1].Value, value, at);
    }
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private async Task<JsonDocument> GetJsonAsync(string url, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CodexTracker");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // Do not follow redirects to a different domain, even with an injected handler.
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Public response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + read > limit) throw new InvalidDataException("Public response too large.");
            bytes.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 24 });
    }
}
