using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;
using IPTVDownloader.Services;

public sealed class WebEngine
{
    private readonly XtreamClient _client = new();
    private readonly SettingsStore _store = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly DownloadService _downloader;
    private readonly Queue<DownloadJob> _pending = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly HashSet<string> _inQueue = new(StringComparer.Ordinal);
    private Dictionary<string, XtreamAccount> _saved = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private XtreamAccount? _account;
    private string _accountName = "";
    private List<MediaItem>? _movies;
    private List<MediaItem>? _series;
    private readonly Dictionary<string, List<MediaItem>> _seasons = new();
    private bool _initialized;
    private bool _workerRunning;
    private DownloadJob? _current;
    private CancellationTokenSource? _cancel;
    private AsyncPauseGate? _pause;

    public WebEngine() { _downloader = new DownloadService(_client); }

    // O ZimaOS aponta o SSD/HD para dentro do contêiner, por exemplo em /storage.
    // O usuário escolhe apenas essa pasta PAI. O DownloadService do C# cria sozinho
    // filmes/, series/<nome>/Temporada NN/ e todos os arquivos.
    private string ResolveParent()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DownloadParent))
            return _store.ResolveDownloadParent(_settings);
        var preferred = Environment.GetEnvironmentVariable("IPTV_DOWNLOAD_PARENT");
        return !string.IsNullOrWhiteSpace(preferred)
            ? Path.GetFullPath(preferred)
            : _store.ResolveDownloadParent(_settings);
    }

    private string ResolveRoot()
    {
        var parent = Path.GetFullPath(ResolveParent());
        var folder = Environment.GetEnvironmentVariable("IPTV_DOWNLOAD_ROOT_NAME");
        if (string.IsNullOrWhiteSpace(folder))
            return SettingsStore.ResolveDownloadRoot(parent); // Compatível com o desktop.
        folder = folder.Trim();
        // Um único nome de pasta, nunca outro caminho absoluto ou relativo.
        if (folder is "." or ".." || Path.GetFileName(folder) != folder ||
            folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Nome da pasta principal inválido.");
        return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(parent)), folder,
            StringComparison.OrdinalIgnoreCase) ? parent : Path.Combine(parent, folder);
    }

    private async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            _saved = await _store.LoadAccountsAsync();
            _settings = await _store.LoadSettingsAsync();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    public async Task<object> GetStateAsync()
    {
        await InitializeAsync();
        lock (_lock)
        {
            return new
            {
                connected = _account is not null,
                account = _accountName,
                savedAccounts = _saved.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                downloadParent = ResolveParent(),
                downloadRoot = ResolveRoot(),
                hideAdult = _settings.HideXxxContent,
                version = "2.0.26-web-beta"
            };
        }
    }

    public async Task<bool> ConnectAsync(XtreamAccount candidate, bool remember)
    {
        await InitializeAsync();
        candidate.Server = XtreamClient.NormalizeServer(candidate.Server);
        if (!Uri.TryCreate(candidate.Server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Servidor inválido");
        if (!await _client.ValidateAsync(candidate)) return false;
        string key = $"{candidate.Username}@{uri.Authority}";
        lock (_lock)
        {
            _account = candidate;
            _accountName = key;
            _movies = null;
            _series = null;
            _seasons.Clear();
        }
        if (remember)
        {
            _saved[key] = candidate;
            await _store.SaveAccountsAsync(_saved);
        }
        return true;
    }

    public async Task<bool> ConnectSavedAsync(string key)
    {
        await InitializeAsync();
        if (!_saved.TryGetValue(key ?? "", out var account)) return false;
        return await ConnectAsync(account, false);
    }

    private XtreamAccount RequireAccount()
    {
        lock (_lock) return _account ?? throw new InvalidOperationException("Conecte sua conta IPTV primeiro.");
    }

    public async Task<List<MediaItem>> GetCatalogAsync(string kind)
    {
        await InitializeAsync();
        var account = RequireAccount();
        await _catalogLock.WaitAsync();
        try
        {
            var result = kind switch
            {
                "movies" => _movies ??= await _client.GetMoviesAsync(account),
                "series" => _series ??= await _client.GetSeriesAsync(account),
                _ => throw new InvalidOperationException("Catálogo inválido")
            };
            return _settings.HideXxxContent
                ? result.Where(i => !IsAdult(i)).ToList()
                : result.ToList();
        }
        finally { _catalogLock.Release(); }
    }

    private static bool IsAdult(MediaItem item)
    {
        var text = TextTools.Normalize($"{item.Name} {item.Group}");
        return new[] { "xxx", "porno", "pornograf", "+18", "adultos", "adult only" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<MediaItem>> GetSeasonsAsync(string id)
    {
        if (_seasons.TryGetValue(id, out var cached)) return cached;
        var account = RequireAccount();
        var series = (await GetCatalogAsync("series")).FirstOrDefault(s => s.SeriesId == id)
            ?? throw new InvalidOperationException("Série não encontrada");
        var seasons = await _client.BuildSeasonsAsync(account, series);
        _seasons[id] = seasons;
        return seasons;
    }

    public async Task<List<MediaItem>> GetEpisodesAsync(string id, int season)
    {
        var seasons = await GetSeasonsAsync(id);
        return seasons.FirstOrDefault(s => s.Season == season)?.Episodes ?? [];
    }

    public static object Card(MediaItem item) => new
    {
        id = item.Identity,
        type = item.Type,
        name = item.Name,
        group = item.Group,
        logo = item.Logo,
        year = item.Year,
        rating = item.Rating,
        genre = item.Genre,
        plot = item.Plot,
        seriesId = item.SeriesId,
        season = item.Season,
        episode = item.EpisodeNumber,
        episodeCount = item.Episodes.Count
    };

    public async Task<int> EnqueueAsync(IReadOnlyList<string> keys)
    {
        await InitializeAsync();
        var account = RequireAccount();
        if (keys.Count is < 1 or > 250) throw new InvalidOperationException("Selecione de 1 a 250 itens por vez.");
        var expanded = new List<MediaItem>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (key.StartsWith("movie:", StringComparison.Ordinal))
            {
                var item = (await GetCatalogAsync("movies")).FirstOrDefault(m => m.Identity == key);
                if (item != null) expanded.Add(item);
            }
            else if (key.StartsWith("series:", StringComparison.Ordinal))
            {
                var id = key["series:".Length..];
                foreach (var season in await GetSeasonsAsync(id)) expanded.AddRange(season.Episodes);
            }
            else if (key.StartsWith("season:", StringComparison.Ordinal))
            {
                var rest = key["season:".Length..];
                var split = rest.LastIndexOf(':');
                if (split > 0 && int.TryParse(rest[(split + 1)..], out var number))
                    expanded.AddRange(await GetEpisodesAsync(rest[..split], number));
            }
            else if (key.StartsWith("episode:", StringComparison.Ordinal))
            {
                var item = _seasons.Values.SelectMany(seasons => seasons).SelectMany(season => season.Episodes)
                    .FirstOrDefault(ep => ep.Identity == key);
                if (item != null) expanded.Add(item);
            }
        }
        var added = 0;
        lock (_lock)
        {
            foreach (var item in expanded.DistinctBy(i => i.Identity))
            {
                // Conta com IP/usuário diferente deve ser uma fila diferente.
                var jobKey = $"{account.Server}\n{account.Username}\n{item.Identity}";
                if (!_inQueue.Add(jobKey)) continue;
                var job = new DownloadJob { Key = jobKey, Name = item.Name, Item = item, Account = account };
                _jobs.Insert(0, job);
                _pending.Enqueue(job);
                added++;
            }
            if (!_workerRunning && _pending.Count > 0)
            {
                _workerRunning = true;
                _ = Task.Run(WorkerAsync);
            }
        }
        return added;
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            DownloadJob job;
            CancellationTokenSource cancel;
            AsyncPauseGate pause;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _workerRunning = false;
                    _current = null;
                    _cancel = null;
                    _pause = null;
                    return;
                }
                job = _pending.Dequeue();
                cancel = new CancellationTokenSource();
                pause = new AsyncPauseGate();
                _current = job;
                _cancel = cancel;
                _pause = pause;
                job.Status = "Baixando";
            }
            try
            {
                var root = ResolveRoot();
                var progress = new Progress<DownloadProgress>(p =>
                {
                    lock (_lock)
                    {
                        if (job.Status is not ("Baixando" or "Pausado")) return;
                        job.Progress = Math.Clamp(p.FileFraction, 0, 1);
                        job.Speed = p.BytesPerSecond;
                        job.Bytes = p.FileBytesDownloaded;
                        job.TotalBytes = p.FileBytesTotal;
                    }
                });
                await _downloader.DownloadBatchAsync([job.Item], job.Account, root, progress, pauseGate: pause, ct: cancel.Token);
                lock (_lock) { job.Status = "Concluído"; job.Progress = 1; }
            }
            catch (OperationCanceledException)
            {
                lock (_lock) job.Status = "Cancelado";
            }
            catch (Exception)
            {
                lock (_lock) job.Status = "Erro";
                // Não expor as URLs de download, que podem conter usuário/senha.
            }
            finally
            {
                lock (_lock)
                {
                    _inQueue.Remove(job.Key);
                    if (ReferenceEquals(_current, job)) _current = null;
                    if (ReferenceEquals(_cancel, cancel)) _cancel = null;
                    if (ReferenceEquals(_pause, pause)) _pause = null;
                }
                cancel.Dispose();
            }
        }
    }

    public object DownloadState()
    {
        lock (_lock)
        {
            return new
            {
                paused = _pause?.IsPaused ?? false,
                pending = _pending.Count,
                working = _current != null,
                jobs = _jobs.Take(200).Select(j => new
                {
                    id = j.Id,
                    name = j.Name,
                    status = j.Status,
                    progress = j.Progress,
                    speed = j.Speed,
                    bytes = j.Bytes,
                    totalBytes = j.TotalBytes
                }).ToList()
            };
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _pause?.Pause();
            if (_current is not null && _pause is not null) _current.Status = "Pausado";
        }
    }
    public void Resume()
    {
        lock (_lock)
        {
            _pause?.Resume();
            if (_current is not null) _current.Status = "Baixando";
        }
    }
    public void Cancel()
    {
        lock (_lock)
        {
            _pause?.Resume();
            _cancel?.Cancel();
        }
    }

    public async Task SaveSettingsAsync(SettingsRequest req)
    {
        await InitializeAsync();
        if (req.DownloadParent != null)
        {
            var dir = req.DownloadParent.Trim();
            if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("Pasta vazia");
            _settings.DownloadParent = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        }
        if (req.HideAdult.HasValue) _settings.HideXxxContent = req.HideAdult.Value;
        await _store.SaveSettingsAsync(_settings);
    }

    private sealed class DownloadJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public MediaItem Item { get; init; } = new();
        public XtreamAccount Account { get; init; } = new();
        public string Status { get; set; } = "Na fila";
        public double Progress { get; set; }
        public double Speed { get; set; }
        public long Bytes { get; set; }
        public long TotalBytes { get; set; }
    }
}
