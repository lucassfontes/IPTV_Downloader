using System.Diagnostics;
using System.Text.Json;
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

    private string QueuePath => Path.Combine(_store.Paths.Data, "download-queue.json");
    private void SaveQueue()
    {
        using (var stream = new FileStream(QueuePath + ".tmp", FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(stream, _jobs);
            stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(QueuePath + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(QueuePath + ".tmp", QueuePath, true);
    }
    private void StartWorker()
    {
        if (!_workerRunning && _pending.Count > 0)
        {
            _workerRunning = true;
            _ = Task.Run(WorkerAsync);
        }
    }
    public Task StartAsync() => InitializeAsync();
    public void ControlJob(string id, string action)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("Download não encontrado.");
            if (action == "resume")
            {
                if (job.Status != "Pausado") throw new InvalidOperationException("Este download não está pausado.");
                if (ReferenceEquals(job, _current)) { Resume(); job.Error = ""; }
                else Retry(id, false);
                return;
            }
            if (action is not ("pause" or "stop")) throw new InvalidOperationException("Ação inválida.");
            if (job.Status is not ("Na fila" or "Baixando" or "Pausado"))
                throw new InvalidOperationException("Este download não está ativo.");
            if (ReferenceEquals(job, _current))
            {
                if (action == "pause") Pause();
                else Cancel();
                return;
            }
            var keep = _pending.Where(j => !ReferenceEquals(j, job)).ToArray();
            _pending.Clear(); foreach (var item in keep) _pending.Enqueue(item);
            _inQueue.Remove(job.Key);
            job.Status = action == "pause" ? "Pausado" : "Cancelado";
            job.Speed = 0;
            SaveQueue();
        }
    }
    public void Retry(string id, bool restart)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("Download não encontrado.");
            if (job.Status is "Na fila" or "Baixando" || _inQueue.Contains(job.Key))
                throw new InvalidOperationException("Este download já está ativo.");
            if (job.Status == "Concluído") throw new InvalidOperationException("Download já concluído.");
            if (restart) { DownloadService.RestartFile(DownloadService.OutputPath(job.Root, job.Item)); job.Connections = _settings.DownloadConnections; }
            job.Status = "Na fila"; job.Error = ""; job.Speed = 0;
            if (restart) { job.Bytes = 0; job.Progress = 0; }
            SaveQueue();
            _inQueue.Add(job.Key); _pending.Enqueue(job); StartWorker();
        }
    }
    public object OpenDownloadFolder(string id)
    {
        string output;
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("Download não encontrado.");
            if (job.Status != "Concluído") throw new InvalidOperationException("A pasta pode ser aberta depois de concluir o download.");
            output = DownloadService.OutputPath(job.Root, job.Item);
        }
        var targetDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            throw new InvalidOperationException("A pasta deste arquivo não foi encontrada.");

        // Navegadores não podem abrir diretamente o gerenciador de arquivos do computador.
        // Em um servidor Docker/ZimaOS, devolve o caminho para copiar em vez de alegar sucesso.
        if (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("container") is not null)
            return new { opened = false, path = targetDirectory, message = "No Docker/ZimaOS, abra esta pasta pelo gerenciador de arquivos do servidor. Caminho disponível para copiar." };

        try
        {
            ProcessStartInfo? info = null;
            if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add(targetDirectory);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    return new { opened = false, path = targetDirectory, message = "O aplicativo está sem sessão gráfica. Abra o caminho manualmente no gerenciador de arquivos." };
                info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                info.ArgumentList.Add(targetDirectory);
            }
            else if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("open") { UseShellExecute = false };
                info.ArgumentList.Add(targetDirectory);
            }
            else return new { opened = false, path = targetDirectory, message = "Sistema sem suporte à abertura automática de pastas." };
            using var process = Process.Start(info);
            if (process is null) throw new InvalidOperationException("Falha ao iniciar o gerenciador de arquivos.");
            if (process.WaitForExit(800) && process.ExitCode != 0)
                throw new InvalidOperationException("O gerenciador de arquivos não conseguiu abrir a pasta.");
            return new { opened = true, path = targetDirectory, message = "Pasta aberta." };
        }
        catch (Exception)
        {
            return new { opened = false, path = targetDirectory, message = "Não foi possível abrir a pasta automaticamente. Copie o caminho para abri-la manualmente." };
        }
    }
    public void DeleteDownload(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("Download não encontrado.");
            if (ReferenceEquals(job, _current) || _inQueue.Contains(job.Key) || job.Status is "Baixando" or "Na fila" or "Pausado")
                throw new InvalidOperationException("Não é possível excluir um download que ainda está ativo.");

            var output = DownloadService.OutputPath(job.Root, job.Item);
            DeleteOutputArtifacts(output, job.Root);
            _jobs.Remove(job);
            SaveQueue();
        }
    }

    private static void DeleteOutputArtifacts(string output, string root)
    {
        foreach (var suffix in new[] { "", ".part", ".merge", ".resume.json", ".verified.json" })
        {
            var path = output + suffix;
            if (File.Exists(path)) File.Delete(path);
        }

        var segments = output + ".segments";
        if (Directory.Exists(segments)) Directory.Delete(segments, true);

        var directory = Path.GetDirectoryName(output);
        var stopRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            var current = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(current, stopRoot, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
    // Limpa apenas recibos de arquivos que a fila já marcou como concluídos.
    // Não toca em recibos de downloads interrompidos, usados na recuperação.
    private static void CleanCompletedReceipt(DownloadJob job)
    {
        if (job.Status != "Concluído") return;
        var output = DownloadService.OutputPath(job.Root, job.Item);
        if (!File.Exists(output)) return;
        var receipt = output + ".verified.json";
        if (!File.Exists(receipt)) return;
        try { File.Delete(receipt); }
        catch (IOException) { /* Tentar novamente na próxima inicialização. */ }
        catch (UnauthorizedAccessException) { /* Não comprometer a fila por uma falha de limpeza. */ }
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
            lock (_lock)
            {
                if (File.Exists(QueuePath))
                {
                    var restored = JsonSerializer.Deserialize<List<DownloadJob>>(File.ReadAllText(QueuePath))
                        ?? throw new IOException("Fila salva inválida; arquivo preservado.");
                    _jobs.AddRange(restored);
                    foreach (var job in restored.AsEnumerable().Reverse())
                    {
                        if (string.IsNullOrEmpty(job.Root)) job.Root = ResolveRoot();
                        job.Speed = 0;
                        var segmented = DownloadService.SegmentedProgress(DownloadService.OutputPath(job.Root, job.Item));
                        if (segmented.Total > 0)
                        { job.Bytes = segmented.Bytes; job.TotalBytes = segmented.Total; job.Progress = Math.Clamp(job.Bytes / (double)job.TotalBytes, 0, 1); }
                        var partial = DownloadService.OutputPath(job.Root, job.Item) + ".part";
                        if (File.Exists(partial))
                        {
                            job.Bytes = new FileInfo(partial).Length;
                            var metadataPath = DownloadService.OutputPath(job.Root, job.Item) + ".resume.json";
                            if (File.Exists(metadataPath))
                            {
                                try
                                {
                                    using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                                    if (metadata.RootElement.TryGetProperty("Total", out var total)) job.TotalBytes = total.GetInt64();
                                }
                                catch (JsonException) { /* Transfer validates metadata before resuming. */ }
                            }
                            job.Progress = job.TotalBytes > 0 ? Math.Clamp(job.Bytes / (double)job.TotalBytes, 0, 1) : 0;
                        }
                        CleanCompletedReceipt(job);
                        if (job.Status is "Baixando" or "Na fila")
                        { job.Status = "Na fila"; _inQueue.Add(job.Key); _pending.Enqueue(job); }
                        else if (job.Status == "Pausado")
                        { job.Error = "Download pausado. Use Continuar quando desejar."; }
                    }
                }
                _initialized = true;
                StartWorker();
            }
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
                downloadConnections = _settings.DownloadConnections,
                version = "2.0.36-web-beta"
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
            foreach (var item in expanded.DistinctBy(i => i.Identity)
                .GroupBy(i => i.IsEpisode ? "series:" + i.SeriesId : i.Identity)
                .SelectMany(group => group.OrderBy(i => i.Season).ThenBy(i => i.IsEpisode && i.EpisodeNumber <= 0 ? int.MaxValue : i.EpisodeNumber)))
            {
                // Conta com IP/usuário diferente deve ser uma fila diferente.
                var jobKey = $"{account.Server}\n{account.Username}\n{item.Identity}";
                if (!_inQueue.Add(jobKey)) continue;
                var job = new DownloadJob { Key = jobKey, Name = item.Name, Item = item, Account = account, Root = ResolveRoot(), Connections = _settings.DownloadConnections };
                _jobs.Insert(0, job);
                _pending.Enqueue(job);
                added++;
            }
            SaveQueue();
            StartWorker();
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
                var root = job.Root;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    lock (_lock)
                    {
                        if (job.Status is not ("Baixando" or "Pausado")) return;
                        job.Progress = Math.Clamp(p.FileFraction, 0, 1);
                        job.Speed = p.BytesPerSecond;
                        job.Bytes = p.FileBytesDownloaded;
                        job.TotalBytes = p.FileBytesTotal;
                        job.Detail = p.Status;
                    }
                });
                await _downloader.DownloadBatchAsync([job.Item], job.Account, root, progress, pauseGate: pause, ct: cancel.Token, connections: job.Connections);
                lock (_lock)
                {
                    job.Status = "Concluído"; job.Progress = 1; job.Error = "";
                    job.Bytes = job.TotalBytes = new FileInfo(DownloadService.OutputPath(root, job.Item)).Length;
                    CleanCompletedReceipt(job);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                lock (_lock) job.Status = "Cancelado";
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    job.Status = "Erro";
                    job.Error = ex is DownloadService.DownloadProblem ? ex.Message
                        : "Transferência interrompida. Verifique a rede e o espaço disponível e tente novamente.";
                }
                // Não expor as URLs de download, que podem conter usuário/senha.
            }
            finally
            {
                lock (_lock)
                {
                    job.Speed = 0;
                    try
                    {
                        var segmented = DownloadService.SegmentedProgress(DownloadService.OutputPath(job.Root, job.Item));
                        if (segmented.Total > 0)
                        { job.Bytes = segmented.Bytes; job.TotalBytes = segmented.Total; job.Progress = Math.Clamp(job.Bytes / (double)job.TotalBytes, 0, 1); }
                        var partial = DownloadService.OutputPath(job.Root, job.Item) + ".part";
                        if (File.Exists(partial))
                        {
                            job.Bytes = new FileInfo(partial).Length;
                            job.Progress = job.TotalBytes > 0 ? Math.Clamp(job.Bytes / (double)job.TotalBytes, 0, 1) : 0;
                        }
                    }
                    catch (IOException) { /* Preserve the latest measured progress. */ }
                    try { SaveQueue(); }
                    catch { job.Status = "Erro"; job.Error = "Não foi possível salvar a fila. Verifique o armazenamento do servidor."; }
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
                currentId = _current?.Id,
                jobs = (_current is null ? Enumerable.Empty<DownloadJob>() : new[] { _current })
                    .Concat(_pending).Concat(_jobs).DistinctBy(j => j.Id).Select(j => new
                {
                    id = j.Id,
                    name = j.Name,
                    logo = j.Item.Logo,
                    status = j.Status,
                    progress = j.Progress,
                    speed = j.Speed,
                    bytes = j.Bytes,
                    totalBytes = j.TotalBytes,
                    error = j.Error,
                    detail = j.Detail,
                    connections = j.Connections
                }).ToList()
            };
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _pause?.Pause();
            if (_current is not null && _pause is not null) { _current.Status = "Pausado"; SaveQueue(); }
        }
    }
    public void Resume()
    {
        lock (_lock)
        {
            _pause?.Resume();
            if (_current is not null) { _current.Status = "Baixando"; SaveQueue(); }
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
        if (req.DownloadConnections.HasValue)
        {
            if (req.DownloadConnections.Value is not (1 or 2 or 4 or 8 or 16))
                throw new InvalidOperationException("Escolha 1, 2, 4, 8 ou 16 conexões.");
            _settings.DownloadConnections = req.DownloadConnections.Value;
        }
        if (req.HideAdult.HasValue) _settings.HideXxxContent = req.HideAdult.Value;
        await _store.SaveSettingsAsync(_settings);
    }

    public sealed class DownloadJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public MediaItem Item { get; init; } = new();
        public XtreamAccount Account { get; init; } = new();
        public int Connections { get; set; } = 1;
        public string Detail { get; set; } = "";
        public string Root { get; set; } = "";
        public string Error { get; set; } = "";
        public string Status { get; set; } = "Na fila";
        public double Progress { get; set; }
        public double Speed { get; set; }
        public long Bytes { get; set; }
        public long TotalBytes { get; set; }
    }
}
