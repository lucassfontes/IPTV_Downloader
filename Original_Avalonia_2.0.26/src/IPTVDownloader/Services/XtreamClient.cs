using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;

namespace IPTVDownloader.Services;

public sealed class XtreamClient
{
    public HttpClient HttpClient { get; }

    public XtreamClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 12,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true
        };

        HttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        HttpClient.DefaultRequestHeaders.UserAgent.Clear();
        HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/152.0 Safari/537.36");
        HttpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        HttpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }

    public static string NormalizeServer(string? server)
    {
        var raw = (server ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // Aceita tanto a URL base (http://servidor:porta) quanto URLs Xtream
        // completas copiadas do navegador/M3U, por exemplo:
        //   http://host:porta/player_api.php?username=...&password=...
        //   http://host:porta/get.php?username=...&password=...&type=m3u_plus
        // e reduz tudo para a raiz correta do painel.
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "http://" + raw;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            var lower = path.ToLowerInvariant();
            var knownEndpoints = new[]
            {
                "/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php"
            };

            foreach (var endpoint in knownEndpoints)
            {
                if (lower.EndsWith(endpoint, StringComparison.Ordinal))
                {
                    path = path[..^endpoint.Length];
                    break;
                }
            }

            var authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return (authority + path).TrimEnd('/');
        }

        // Fallback para entradas malformadas: remove query string e endpoints
        // conhecidos sem tentar inventar credenciais ou caminhos.
        var queryIndex = raw.IndexOf('?');
        if (queryIndex >= 0) raw = raw[..queryIndex];
        foreach (var endpoint in new[] { "/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php" })
        {
            if (raw.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[..^endpoint.Length];
                break;
            }
        }
        return raw.TrimEnd('/');
    }

    public static string ApiUrl(XtreamAccount account, string? action = null, params (string key, string value)[] args)
    {
        var query = new List<string>
        {
            $"username={Uri.EscapeDataString(account.Username)}",
            $"password={Uri.EscapeDataString(account.Password)}"
        };
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action)}");
        query.AddRange(args.Select(a => $"{Uri.EscapeDataString(a.key)}={Uri.EscapeDataString(a.value)}"));
        return $"{NormalizeServer(account.Server)}/player_api.php?{string.Join('&', query)}";
    }

    public static string MovieUrl(XtreamAccount a, string id, string ext = "mp4") =>
        $"{NormalizeServer(a.Server)}/movie/{Uri.EscapeDataString(a.Username)}/{Uri.EscapeDataString(a.Password)}/{id}.{ext.TrimStart('.')}";

    public static string EpisodeUrl(XtreamAccount a, string id, string ext = "mp4") =>
        $"{NormalizeServer(a.Server)}/series/{Uri.EscapeDataString(a.Username)}/{Uri.EscapeDataString(a.Password)}/{id}.{ext.TrimStart('.')}";

    public async Task<bool> ValidateAsync(XtreamAccount account, CancellationToken ct = default)
    {
        var node = await GetNodeAsync(ApiUrl(account), ct);
        var auth = node?["user_info"]?["auth"]?.ToString();
        return auth is "1" or "true" or "True";
    }

    public async Task<List<MediaItem>> GetMoviesAsync(XtreamAccount account, CancellationToken ct = default)
    {
        var streamsTask = GetNodeAsync(ApiUrl(account, "get_vod_streams"), ct);
        var categoriesTask = GetCategoryMapAsync(account, "get_vod_categories", ct);
        await Task.WhenAll(streamsTask, categoriesTask);

        var items = new List<MediaItem>();
        if (await streamsTask is not JsonArray array) return items;
        var categories = await categoriesTask;

        foreach (var n in array.OfType<JsonObject>())
        {
            var name = S(n, "name", "Filme sem nome");
            var categoryId = S(n, "category_id");
            items.Add(new MediaItem
            {
                Type = "movie",
                Name = name,
                IptvName = name,
                CategoryId = categoryId,
                Group = ResolveCategoryName(categories, categoryId),
                StreamId = S(n, "stream_id"),
                Logo = S(n, "stream_icon"),
                ContainerExtension = S(n, "container_extension", "mp4"),
                DirectSource = S(n, "direct_source"),
                Year = S(n, "year"),
                Rating = S(n, "rating"),
                Genre = S(n, "genre"),
                Plot = S(n, "plot"),
                ReleaseDate = S(n, "release_date")
            });
        }

        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<List<MediaItem>> GetSeriesAsync(XtreamAccount account, CancellationToken ct = default)
    {
        var streamsTask = GetNodeAsync(ApiUrl(account, "get_series"), ct);
        var categoriesTask = GetCategoryMapAsync(account, "get_series_categories", ct);
        await Task.WhenAll(streamsTask, categoriesTask);

        var items = new List<MediaItem>();
        if (await streamsTask is not JsonArray array) return items;
        var categories = await categoriesTask;

        foreach (var n in array.OfType<JsonObject>())
        {
            var categoryId = S(n, "category_id");
            items.Add(new MediaItem
            {
                Type = "series",
                Name = S(n, "name", "Série sem nome"),
                CategoryId = categoryId,
                Group = ResolveCategoryName(categories, categoryId),
                SeriesId = S(n, "series_id"),
                Logo = S(n, "cover", S(n, "stream_icon")),
                Year = S(n, "year"),
                Genre = S(n, "genre"),
                Plot = S(n, "plot"),
                Rating = S(n, "rating"),
                Cast = S(n, "cast"),
                Director = S(n, "director"),
                EpisodeRunTime = S(n, "episode_run_time"),
                ReleaseDate = S(n, "releaseDate", S(n, "release_date"))
            });
        }

        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<Dictionary<string, string>> GetCategoryMapAsync(XtreamAccount account, string action, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonNode? node;
        try
        {
            node = await GetNodeAsync(ApiUrl(account, action), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Alguns painéis Xtream não expõem o endpoint de categorias para todas as contas.
            // Isso não deve impedir o catálogo de filmes/séries de carregar.
            return result;
        }

        if (node is not JsonArray array) return result;

        foreach (var n in array.OfType<JsonObject>())
        {
            var id = S(n, "category_id");
            var name = S(n, "category_name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) result[id] = name.Trim();
        }
        return result;
    }

    private static string ResolveCategoryName(Dictionary<string, string> categories, string categoryId)
    {
        if (!string.IsNullOrWhiteSpace(categoryId) && categories.TryGetValue(categoryId, out var name)) return name;
        return "Sem categoria";
    }

    public Task<JsonObject?> GetVodInfoAsync(XtreamAccount account, string streamId, CancellationToken ct = default) =>
        GetObjectAsync(ApiUrl(account, "get_vod_info", ("vod_id", streamId)), ct);

    public Task<JsonObject?> GetSeriesInfoAsync(XtreamAccount account, string seriesId, CancellationToken ct = default) =>
        GetObjectAsync(ApiUrl(account, "get_series_info", ("series_id", seriesId)), ct);

    public async Task<List<MediaItem>> BuildSeasonsAsync(XtreamAccount account, MediaItem series, JsonObject? payload = null, CancellationToken ct = default)
    {
        payload ??= await GetSeriesInfoAsync(account, series.SeriesId, ct);
        if (payload is null) return [];

        ApplySeriesMetadata(series, payload);
        var seasons = new List<MediaItem>();
        var episodesNode = payload["episodes"];
        if (episodesNode is JsonObject bySeason)
        {
            foreach (var kv in bySeason)
            {
                if (kv.Value is not JsonArray eps) continue;
                var seasonNo = int.TryParse(kv.Key, out var sn) ? sn : 0;
                var episodeItems = BuildEpisodes(series, seasonNo, eps);
                seasons.Add(new MediaItem
                {
                    Type = "season",
                    Name = $"Temporada {seasonNo}",
                    Group = $"{episodeItems.Count} episódios",
                    SeriesId = series.SeriesId,
                    SeriesName = series.Name,
                    Season = seasonNo,
                    Logo = series.Logo,
                    Episodes = episodeItems,
                    MetaText = $"{episodeItems.Count} episódios"
                });
            }
        }
        else if (episodesNode is JsonArray flat)
        {
            var grouped = flat.OfType<JsonObject>().GroupBy(e => I(e, "season"));
            foreach (var group in grouped)
            {
                var episodeItems = BuildEpisodes(series, group.Key, new JsonArray(group.Select(x => (JsonNode?)x.DeepClone()).ToArray()));
                seasons.Add(new MediaItem
                {
                    Type = "season",
                    Name = $"Temporada {group.Key}",
                    Group = $"{episodeItems.Count} episódios",
                    SeriesId = series.SeriesId,
                    SeriesName = series.Name,
                    Season = group.Key,
                    Logo = series.Logo,
                    Episodes = episodeItems,
                    MetaText = $"{episodeItems.Count} episódios"
                });
            }
        }

        return seasons.OrderBy(s => s.Season).ToList();
    }

    private static List<MediaItem> BuildEpisodes(MediaItem series, int seasonNo, JsonArray eps)
    {
        var result = new List<MediaItem>();
        foreach (var ep in eps.OfType<JsonObject>())
        {
            var info = ep["info"] as JsonObject;
            var epNo = I(ep, "episode_num", I(ep, "episode"));
            var rawTitle = S(ep, "title", S(info, "name", S(ep, "name", $"Episódio {epNo}")));
            var display = $"{series.Name} - S{seasonNo:00}E{epNo:00} - {rawTitle}";
            result.Add(new MediaItem
            {
                Type = "episode",
                Name = display,
                Group = $"Temporada {seasonNo}",
                EpisodeId = S(ep, "id", S(ep, "episode_id")),
                SeriesId = series.SeriesId,
                SeriesName = series.Name,
                Season = seasonNo,
                EpisodeNumber = epNo,
                ContainerExtension = S(ep, "container_extension", S(info, "container_extension", "mp4")),
                DirectSource = S(ep, "direct_source", S(info, "direct_source")),
                Logo = S(info, "movie_image", S(info, "cover", series.Logo)),
                Plot = S(info, "plot", S(info, "description")),
                Rating = S(info, "rating"),
                Duration = S(info, "duration"),
                DurationSeconds = L(info, "duration_secs", L(info, "duration_sec")),
                ReleaseDate = S(info, "releasedate", S(info, "releaseDate"))
            });
        }
        return result.OrderBy(e => e.EpisodeNumber).ToList();
    }

    public static void ApplyMovieMetadata(MediaItem item, JsonObject payload)
    {
        var info = payload["info"] as JsonObject ?? new JsonObject();
        var movie = payload["movie_data"] as JsonObject ?? new JsonObject();
        item.Year = First(S(info, "year"), S(movie, "year"), item.Year);
        item.Genre = First(S(info, "genre"), item.Genre);
        item.Plot = First(S(info, "plot"), S(info, "description"), item.Plot);
        item.Rating = First(S(info, "rating"), item.Rating);
        item.Cast = First(S(info, "cast"), S(info, "actors"), item.Cast);
        item.Director = First(S(info, "director"), item.Director);
        item.Duration = First(S(info, "duration"), S(info, "runtime"), item.Duration);
        item.DurationSeconds = FirstPositive(
            L(info, "duration_secs"),
            L(info, "duration_sec"),
            L(movie, "duration_secs"),
            item.DurationSeconds);
        item.ReleaseDate = First(S(info, "releasedate"), S(info, "releaseDate"), item.ReleaseDate);
        item.Logo = First(S(info, "movie_image"), S(info, "cover_big"), S(info, "cover"), item.Logo);
    }

    public static void ApplySeriesMetadata(MediaItem item, JsonObject payload)
    {
        var info = payload["info"] as JsonObject ?? new JsonObject();
        item.Year = First(S(info, "year"), item.Year);
        item.Genre = First(S(info, "genre"), item.Genre);
        item.Plot = First(S(info, "plot"), S(info, "description"), item.Plot);
        item.Rating = First(S(info, "rating"), item.Rating);
        item.Cast = First(S(info, "cast"), S(info, "actors"), item.Cast);
        item.Director = First(S(info, "director"), item.Director);
        item.EpisodeRunTime = First(S(info, "episode_run_time"), item.EpisodeRunTime);
        item.ReleaseDate = First(S(info, "releaseDate"), S(info, "releasedate"), item.ReleaseDate);
        item.Logo = First(S(info, "cover"), S(info, "cover_big"), item.Logo);
        item.SeasonCount = CountSeasons(payload);
    }

    public static int CountSeasons(JsonObject payload)
    {
        if (payload["episodes"] is JsonObject obj) return obj.Count;
        if (payload["seasons"] is JsonArray arr) return arr.Count;
        return 0;
    }

    public static string S(JsonObject? obj, string name, string fallback = "")
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null) return fallback;
        var s = node.ToString();
        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }

    public static long L(JsonObject? obj, string key, long fallback = 0)
    {
        if (obj is null || obj[key] is null) return fallback;
        return long.TryParse(obj[key]!.ToString(), out var value) ? value : fallback;
    }

    private static long FirstPositive(params long[] values) =>
        values.FirstOrDefault(v => v > 0);

    public static int I(JsonObject? obj, string name, int fallback = 0) =>
        int.TryParse(S(obj, name), out var value) ? value : fallback;

    public static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private async Task<JsonNode?> GetNodeAsync(string url, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<JsonObject?> GetObjectAsync(string url, CancellationToken ct) =>
        await GetNodeAsync(url, ct) as JsonObject;
}
