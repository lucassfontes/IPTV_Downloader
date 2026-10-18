using System.Text.Json;
using IPTVDownloader.Models;

namespace IPTVDownloader.Services;

public sealed class AppPaths
{
    public string Root { get; }
    public string Data { get; }
    public string Cache { get; }
    public string LogoCache { get; }
    public string AccountsFile => Path.Combine(Data, "contas.json");
    public string SettingsFile => Path.Combine(Data, "config.json");
    public string FavoritesFile => Path.Combine(Data, "favoritos.json");
    public string HistoryFile => Path.Combine(Data, "historico.json");

    public AppPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Root = Path.Combine(local, "iptv-downloader");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(xdg))
                xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            Root = Path.Combine(xdg, "iptv-downloader");
        }

        Data = Path.Combine(Root, "data");
        Cache = Path.Combine(Root, "cache");
        LogoCache = Path.Combine(Cache, "logos-csharp");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(LogoCache);
    }
}

public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public AppPaths Paths { get; } = new();

    public async Task<Dictionary<string, XtreamAccount>> LoadAccountsAsync()
    {
        var result = await ReadAsync<Dictionary<string, XtreamAccount>>(Paths.AccountsFile)
                     ?? new Dictionary<string, XtreamAccount>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, XtreamAccount>(result, StringComparer.OrdinalIgnoreCase);
    }

    public Task SaveAccountsAsync(Dictionary<string, XtreamAccount> accounts) => WriteAsync(Paths.AccountsFile, accounts);

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var settings = await ReadAsync<AppSettings>(Paths.SettingsFile) ?? new AppSettings();

        // Compatibilidade com o estado salvo pela versão Python 1.1.54.
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Paths.SettingsFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("last_navigation_by_account", out var navs) && navs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in navs.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        settings.LastNavigationByAccount.TryGetValue(prop.Name, out var nav);
                        nav ??= new NavigationState();
                        nav.View = GetString(prop.Value, "view", nav.View);
                        nav.Kind = GetString(prop.Value, "kind", nav.Kind);
                        nav.SeriesId = GetString(prop.Value, "series_id", nav.SeriesId);
                        nav.SeriesName = GetString(prop.Value, "series_name", nav.SeriesName);
                        nav.Query = GetString(prop.Value, "query", nav.Query);
                        nav.AlphaFilter = GetString(prop.Value, "alpha_filter", nav.AlphaFilter);
                        if (prop.Value.TryGetProperty("season", out var season) && season.TryGetInt32(out var seasonNo))
                            nav.Season = seasonNo;
                        if (prop.Value.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                        {
                            if (state.TryGetProperty("last_page", out var page) && page.TryGetInt32(out var p))
                                nav.Page = Math.Max(1, p);
                            if (state.TryGetProperty("page_scrolls", out var scrolls) && scrolls.ValueKind == JsonValueKind.Object)
                            {
                                var key = nav.Page.ToString();
                                if (scrolls.TryGetProperty(key, out var fraction) && fraction.TryGetDouble(out var f))
                                    nav.ScrollOffset = Math.Max(0, f) * 10000.0; // fallback aproximado na migração
                            }
                        }
                        settings.LastNavigationByAccount[prop.Name] = nav;
                    }
                }
            }
        }
        catch
        {
            // O estado antigo é opcional; falha na migração não bloqueia o app.
        }

        return settings;
    }

    private static string GetString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(Paths.SettingsFile, settings);

    public async Task<Dictionary<string, List<MediaItem>>> LoadFavoritesAsync() =>
        await ReadAsync<Dictionary<string, List<MediaItem>>>(Paths.FavoritesFile)
        ?? new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);

    public Task SaveFavoritesAsync(Dictionary<string, List<MediaItem>> favorites) => WriteAsync(Paths.FavoritesFile, favorites);

    public async Task<Dictionary<string, List<MediaItem>>> LoadHistoryAsync() =>
        await ReadAsync<Dictionary<string, List<MediaItem>>>(Paths.HistoryFile)
        ?? new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);

    public Task SaveHistoryAsync(Dictionary<string, List<MediaItem>> history) => WriteAsync(Paths.HistoryFile, history);

    public string ResolveDownloadParent(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DownloadParent))
            return Environment.ExpandEnvironmentVariables(settings.DownloadParent.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        if (!string.IsNullOrWhiteSpace(settings.DownloadRootLegacy))
        {
            var root = Environment.ExpandEnvironmentVariables(settings.DownloadRootLegacy.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            if (Path.GetFileName(root).Equals("iptv_dowload", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(root)?.FullName ?? root;
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return downloads;
    }

    public static string ResolveDownloadRoot(string parent)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(parent)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : parent);
        return Path.GetFileName(full).Equals("iptv_dowload", StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(full, "iptv_dowload");
    }

    private async Task<T?> ReadAsync<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json);
        }
        catch
        {
            return default;
        }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, value, _json);
        File.Move(tmp, path, true);
    }
}
