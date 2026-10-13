using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Media;
using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;
using IPTVDownloader.Services;

namespace IPTVDownloader.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    public const string Version = "2.0.26";
    public const int PageSize = 30;

    private readonly SettingsStore _store;
    private readonly XtreamClient _xtream;
    private readonly DownloadService _downloader;
    private readonly ImageCacheService _images;
    private Dictionary<string, XtreamAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MediaItem>> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MediaItem>> _history = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();

    private List<MediaItem> _movies = [];
    private List<MediaItem> _series = [];
    private List<MediaItem> _seasons = [];
    private List<MediaItem> _episodes = [];
    private List<MediaItem> _categories = [];
    private List<MediaItem> _filtered = [];
    private readonly Dictionary<string, MediaItem> _selection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject?> _metadataCache = new(StringComparer.Ordinal);

    private CancellationTokenSource? _metadataCts;
    private CancellationTokenSource? _downloadCts;
    private readonly AsyncPauseGate _pauseGate = new();

    private string _selectedAccount = "";
    private string _server = "";
    private string _username = "";
    private string _password = "";
    private string _downloadParent = "";
    private bool _configVisible = true;
    private string _activeConfigTab = "Conexão";
    private string _contentKind = "";
    private string _currentView = "catalog";
    private string _searchText = "";
    private string _alphaFilter = "";
    private string _status = "Informe Servidor, Usuário e Senha.";
    private string _counter = "0 conteúdos";
    private int _currentPage = 1;
    private int _pageCount = 1;
    private double _scrollOffset;
    private double _progress;
    private string _speedText = "Velocidade: --";
    private string _sizeText = "Arquivo: -- / --";
    private string _remainingTimeText = "Tempo restante: --";
    private string _overallText = "Total: 0.0% concluído • Falta: 100.0%";
    private bool _isDownloading;
    private bool _isContentLoading;
    private bool _searchPerformed;
    private bool _shutdownAfterBatch;
    private bool _removeCompletedFromSelection;
    private bool _hideXxxContent = true;
    private MediaItem? _currentSeries;
    private MediaItem? _currentSeason;
    private string _selectedCategoryId = "";
    private string _selectedCategoryName = "";

    public MainWindowViewModel(SettingsStore store, XtreamClient xtream, DownloadService downloader, ImageCacheService images)
    {
        _store = store;
        _xtream = xtream;
        _downloader = downloader;
        _images = images;
        VisibleItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowNoSearchResults));

        OpenSavedAccountCommand = new AsyncRelayCommand(OpenSavedAccountAsync);
        SaveAccountCommand = new AsyncRelayCommand(SaveAccountAsync);
        ValidateConnectionCommand = new AsyncRelayCommand(ValidateConnectionAsync);
        DeleteAccountCommand = new AsyncRelayCommand(DeleteAccountAsync);
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshCatalogAsync);
        ToggleConfigCommand = new RelayCommand(() => ConfigVisible = !ConfigVisible);
        SelectConfigTabCommand = new RelayCommand(p => SelectConfigTab(p?.ToString() ?? "Conexão"));

        MoviesCommand = new AsyncRelayCommand(() => LoadContentAsync("movies"));
        SeriesCommand = new AsyncRelayCommand(() => LoadContentAsync("series"));
        FavoritesCommand = new AsyncRelayCommand(() => LoadContentAsync("favorites"));
        HistoryCommand = new AsyncRelayCommand(() => LoadContentAsync("history"));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        PrevPageCommand = new AsyncRelayCommand(PrevPageAsync);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync);
        BackCommand = new AsyncRelayCommand(BackAsync);
        SelectVisibleCommand = new RelayCommand(SelectVisible);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => _selection.Count > 0 && !IsDownloading);
        PauseCommand = new RelayCommand(TogglePause, () => IsDownloading);
        CancelCommand = new RelayCommand(CancelDownload, () => IsDownloading);
        AlphaCommand = new AsyncRelayCommand(p => SetAlphaAsync(p?.ToString() ?? ""));
    }

    public ObservableCollection<string> SavedAccounts { get; } = [];
    public ObservableCollection<MediaItem> VisibleItems { get; } = [];
    public IReadOnlyList<string> Alphabet { get; } = ["•", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    public string SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }
    public string Server { get => _server; set => SetProperty(ref _server, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string DownloadParent
    {
        get => _downloadParent;
        private set
        {
            if (SetProperty(ref _downloadParent, value))
                OnPropertyChanged(nameof(DownloadRoot));
        }
    }
    public string DownloadRoot => SettingsStore.ResolveDownloadRoot(DownloadParent);

    public bool ConfigVisible
    {
        get => _configVisible;
        set
        {
            if (SetProperty(ref _configVisible, value))
            {
                OnPropertyChanged(nameof(ConfigToggleText));
                OnPropertyChanged(nameof(IsListConfigVisible));
                OnPropertyChanged(nameof(IsConnectionConfigVisible));
                OnPropertyChanged(nameof(IsDownloadsConfigVisible));
            }
        }
    }

    public string ActiveConfigTab
    {
        get => _activeConfigTab;
        private set
        {
            if (SetProperty(ref _activeConfigTab, value))
            {
                OnPropertyChanged(nameof(IsListConfigVisible));
                OnPropertyChanged(nameof(IsConnectionConfigVisible));
                OnPropertyChanged(nameof(IsDownloadsConfigVisible));
                OnPropertyChanged(nameof(ListTabColor));
                OnPropertyChanged(nameof(ConnectionTabColor));
                OnPropertyChanged(nameof(DownloadsTabColor));
            }
        }
    }

    public bool IsListConfigVisible => ConfigVisible && ActiveConfigTab == "Lista";
    public bool IsConnectionConfigVisible => ConfigVisible && ActiveConfigTab == "Conexão";
    public bool IsDownloadsConfigVisible => ConfigVisible && ActiveConfigTab == "Downloads";
    public string ConfigToggleText => ConfigVisible ? "⚙ Ocultar configuração" : "⚙ Mostrar configuração";
    public IBrush ListTabColor => Brush.Parse(ActiveConfigTab == "Lista" ? "#1f4b7a" : "#223146");
    public IBrush ConnectionTabColor => Brush.Parse(ActiveConfigTab == "Conexão" ? "#1f4b7a" : "#223146");
    public IBrush DownloadsTabColor => Brush.Parse(ActiveConfigTab == "Downloads" ? "#1f4b7a" : "#223146");

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchPerformed = false;
                OnPropertyChanged(nameof(ShowNoSearchResults));
            }
        }
    }
    public string AlphaFilter
    {
        get => _alphaFilter;
        private set
        {
            if (SetProperty(ref _alphaFilter, value)) OnPropertyChanged(nameof(ActiveAlphaDisplay));
        }
    }
    public string ActiveAlphaDisplay => string.IsNullOrWhiteSpace(AlphaFilter) ? "•" : AlphaFilter.ToUpperInvariant();
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Counter { get => _counter; set => SetProperty(ref _counter, value); }
    public int CurrentPage { get => _currentPage; private set { if (SetProperty(ref _currentPage, value)) OnPropertyChanged(nameof(PageText)); } }
    public int PageCount { get => _pageCount; private set { if (SetProperty(ref _pageCount, value)) OnPropertyChanged(nameof(PageText)); } }
    public string PageText => $"Página {CurrentPage}/{PageCount}";
    public string PageSizeText => $"{PageSize}/página";
    public double ScrollOffset { get => _scrollOffset; private set => SetProperty(ref _scrollOffset, value); }
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressBrush));
        }
    }
    public IBrush ProgressBrush => Brush.Parse(Progress >= 0.9999 ? "#22c55e" : "#3b82f6");
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }
    public string RemainingTimeText { get => _remainingTimeText; private set => SetProperty(ref _remainingTimeText, value); }
    public string OverallText { get => _overallText; private set => SetProperty(ref _overallText, value); }
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(PauseText));
                (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public string PauseText => _pauseGate.IsPaused ? "Continuar" : "Pausar";
    public bool ShutdownAfterBatch { get => _shutdownAfterBatch; set => SetProperty(ref _shutdownAfterBatch, value); }
    public bool RemoveCompletedFromSelection
    {
        get => _removeCompletedFromSelection;
        set
        {
            if (SetProperty(ref _removeCompletedFromSelection, value))
            {
                _settings.RemoveCompletedFromSelection = value;
                _ = _store.SaveSettingsAsync(_settings);
            }
        }
    }
    public bool HideXxxContent
    {
        get => _hideXxxContent;
        set
        {
            if (SetProperty(ref _hideXxxContent, value))
            {
                _settings.HideXxxContent = value;

                if (value)
                {
                    var hiddenKeys = _selection
                        .Where(pair => IsXxxContent(pair.Value))
                        .Select(pair => pair.Key)
                        .ToList();
                    foreach (var key in hiddenKeys) _selection.Remove(key);
                    if (hiddenKeys.Count > 0)
                    {
                        OnPropertyChanged(nameof(SelectedCount));
                        OnPropertyChanged(nameof(DownloadSelectedText));
                        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    }
                }

                _ = _store.SaveSettingsAsync(_settings);
                _ = ApplyFiltersAsync();
            }
        }
    }
    public int SelectedCount => _selection.Count;
    public string DownloadSelectedText => $"Baixar selecionados ({SelectedCount})";
    public bool IsContentLoading
    {
        get => _isContentLoading;
        private set
        {
            if (SetProperty(ref _isContentLoading, value))
                OnPropertyChanged(nameof(ShowNoSearchResults));
        }
    }
    public bool ShowNoSearchResults => !IsContentLoading && _searchPerformed && VisibleItems.Count == 0;
    public string ContentHeader => _searchPerformed && !string.IsNullOrWhiteSpace(SearchText)
        ? "Busca • Filmes e Séries"
        : _currentView switch
    {
        "categories" => _contentKind == "series" ? "Categorias de Séries" : "Categorias de Filmes",
        "category" => string.IsNullOrWhiteSpace(_selectedCategoryName)
            ? (_contentKind == "series" ? "Séries" : "Filmes")
            : $"{(_contentKind == "series" ? "Séries" : "Filmes")} • {_selectedCategoryName}",
        "seasons" => _currentSeries is null ? "Temporadas" : $"Temporadas • {_currentSeries.Name}",
        "episodes" => _currentSeason is null ? "Episódios" : $"Episódios • {_currentSeason.Name}",
        _ => _contentKind switch
        {
            "series" => "Séries",
            "favorites" => "Favoritos",
            "history" => "Histórico",
            _ => "Filmes"
        }
    };

    public ICommand OpenSavedAccountCommand { get; }
    public ICommand SaveAccountCommand { get; }
    public ICommand ValidateConnectionCommand { get; }
    public ICommand DeleteAccountCommand { get; }
    public ICommand RefreshCatalogCommand { get; }
    public ICommand ToggleConfigCommand { get; }
    public ICommand SelectConfigTabCommand { get; }
    public ICommand MoviesCommand { get; }
    public ICommand SeriesCommand { get; }
    public ICommand FavoritesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AlphaCommand { get; }

    public event Action<double>? ScrollRestoreRequested;

    public async Task InitializeAsync()
    {
        _accounts = await _store.LoadAccountsAsync();
        _settings = await _store.LoadSettingsAsync();
        _favorites = await _store.LoadFavoritesAsync();
        _history = await _store.LoadHistoryAsync();
        DownloadParent = _store.ResolveDownloadParent(_settings);
        _removeCompletedFromSelection = _settings.RemoveCompletedFromSelection;
        _hideXxxContent = _settings.HideXxxContent;
        RefreshAccountsList();

        if (_accounts.Count == 0)
        {
            SelectConfigTab("Conexão");
            ConfigVisible = true;
            Status = "Nenhuma lista salva. Informe os dados da conexão.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastOpenedAccount) && _accounts.ContainsKey(_settings.LastOpenedAccount))
        {
            SelectedAccount = _settings.LastOpenedAccount;
            await OpenSavedAccountAsync(restore: true);
            return;
        }

        SelectConfigTab("Lista");
        ConfigVisible = true;
        SelectedAccount = SavedAccounts.FirstOrDefault() ?? "";
        Status = "Selecione uma lista salva.";
    }

    public void SelectConfigTab(string tab)
    {
        if (tab is not ("Lista" or "Conexão" or "Downloads")) tab = "Conexão";
        ActiveConfigTab = tab;
        ConfigVisible = true;
    }

    public async Task SetDownloadParentAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        DownloadParent = path;
        OnPropertyChanged(nameof(DownloadRoot));
        _settings.DownloadParent = path;
        _settings.DownloadRootLegacy = SettingsStore.ResolveDownloadRoot(path);
        await _store.SaveSettingsAsync(_settings);
        Status = $"Pasta de downloads: {DownloadRoot}";
    }

    public void UpdateScrollOffset(double offset) => ScrollOffset = Math.Max(0, offset);

    public async Task SaveStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedAccount)) return;
        if (!string.IsNullOrWhiteSpace(_contentKind))
            SaveNavigationSnapshot();
        await _store.SaveSettingsAsync(_settings);
    }

    private XtreamAccount CurrentAccount() => new()
    {
        Server = XtreamClient.NormalizeServer(Server),
        Username = Username.Trim(),
        Password = Password
    };

    private async Task OpenSavedAccountAsync() => await OpenSavedAccountAsync(restore: false);

    private async Task OpenSavedAccountAsync(bool restore)
    {
        if (!_accounts.TryGetValue(SelectedAccount, out var account))
        {
            Status = "Selecione uma lista salva.";
            return;
        }

        IsContentLoading = true;
        _searchPerformed = false;
        VisibleItems.Clear();
        await Task.Yield();

        Server = account.Server;
        Username = account.Username;
        Password = account.Password;
        _settings.LastOpenedAccount = SelectedAccount;
        await _store.SaveSettingsAsync(_settings);
        ConfigVisible = false;
        _contentKind = "";
        _currentView = "catalog";
        _currentSeries = null;
        _currentSeason = null;
        _movies = [];
        _series = [];
        _seasons = [];
        _episodes = [];
        SearchText = "";
        AlphaFilter = "";
        VisibleItems.Clear();
        Counter = "0 conteúdos";
        OnPropertyChanged(nameof(ContentHeader));

        if (!restore)
        {
            IsContentLoading = false;
            Status = $"Lista \"{SelectedAccount}\" aberta. Escolha Filmes ou Séries.";
            return;
        }

        if (_settings.LastNavigationByAccount.TryGetValue(SelectedAccount, out var nav))
        {
            var kind = nav.Kind is "movies" or "series" or "favorites" or "history" ? nav.Kind : "movies";
            await LoadContentAsync(kind, saveState: false);
            await RestoreNavigationAsync(nav);
            IsContentLoading = false;
            Status = $"Lista \"{SelectedAccount}\" restaurada.";
            return;
        }

        if (_settings.LastContentByAccount.TryGetValue(SelectedAccount, out var lastKind))
        {
            await LoadContentAsync(lastKind, saveState: false);
            IsContentLoading = false;
            Status = $"Lista \"{SelectedAccount}\" restaurada em {ContentHeader}.";
            return;
        }

        IsContentLoading = false;
        Status = $"Lista \"{SelectedAccount}\" aberta. Escolha Filmes ou Séries.";
    }

    private async Task SaveAccountAsync()
    {
        var account = CurrentAccount();
        if (string.IsNullOrWhiteSpace(account.Server) || string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Password))
        {
            Status = "Preencha Servidor, Usuário e Senha.";
            return;
        }

        _accounts[account.Username] = account;
        await _store.SaveAccountsAsync(_accounts);
        SelectedAccount = account.Username;
        RefreshAccountsList();
        Status = $"Lista \"{account.Username}\" salva.";
    }

    private async Task ValidateConnectionAsync()
    {
        var account = CurrentAccount();
        if (string.IsNullOrWhiteSpace(account.Server) || string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Password))
        {
            Status = "Preencha Servidor, Usuário e Senha.";
            return;
        }

        Status = "Validando conexão...";
        try
        {
            if (!await _xtream.ValidateAsync(account))
            {
                Status = "A lista não foi autenticada pelo servidor.";
                return;
            }

            _accounts[account.Username] = account;
            await _store.SaveAccountsAsync(_accounts);
            SelectedAccount = account.Username;
            _settings.LastOpenedAccount = account.Username;
            await _store.SaveSettingsAsync(_settings);
            RefreshAccountsList();
            ConfigVisible = false;
            Status = "Conexão válida. Escolha Filmes ou Séries.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Status = "Erro ao conectar: servidor respondeu 404. Verifique a URL base do painel (ex.: http://servidor:porta). URLs completas como /get.php ou /player_api.php agora são ajustadas automaticamente.";
        }
        catch (Exception ex)
        {
            Status = $"Erro ao conectar: {ex.Message}";
        }
    }

    private async Task DeleteAccountAsync()
    {
        var name = SelectedAccount;
        if (string.IsNullOrWhiteSpace(name) || !_accounts.Remove(name)) return;
        await _store.SaveAccountsAsync(_accounts);
        _settings.LastContentByAccount.Remove(name);
        _settings.LastNavigationByAccount.Remove(name);
        if (_settings.LastOpenedAccount.Equals(name, StringComparison.OrdinalIgnoreCase)) _settings.LastOpenedAccount = "";
        await _store.SaveSettingsAsync(_settings);
        RefreshAccountsList();
        SelectedAccount = SavedAccounts.FirstOrDefault() ?? "";
        Status = $"Lista \"{name}\" excluída.";
    }

    private async Task RefreshCatalogAsync()
    {
        if (string.IsNullOrWhiteSpace(_contentKind) || _contentKind is "favorites" or "history")
        {
            Status = "Abra Filmes ou Séries para atualizar o catálogo.";
            return;
        }
        if (_contentKind == "movies") _movies = [];
        if (_contentKind == "series") _series = [];
        await LoadContentAsync(_contentKind);
    }

    private void RefreshAccountsList()
    {
        var current = SelectedAccount;
        SavedAccounts.Clear();
        foreach (var name in _accounts.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) SavedAccounts.Add(name);
        if (!string.IsNullOrWhiteSpace(current) && SavedAccounts.Contains(current)) SelectedAccount = current;
        else if (SavedAccounts.Count > 0) SelectedAccount = SavedAccounts[0];
    }

    private async Task LoadContentAsync(string kind, bool saveState = true)
    {
        if (kind is not ("movies" or "series" or "favorites" or "history")) return;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Server))
        {
            Status = "Abra ou carregue uma lista primeiro.";
            return;
        }

        if (saveState) await SaveStateAsync();
        IsContentLoading = true;
        _searchPerformed = false;
        VisibleItems.Clear();
        await Task.Yield();

        _contentKind = kind;
        _currentView = kind is "movies" or "series" ? "categories" : "catalog";
        _currentSeries = null;
        _currentSeason = null;
        _selectedCategoryId = "";
        _selectedCategoryName = "";
        SearchText = "";
        AlphaFilter = "";
        CurrentPage = 1;
        ScrollOffset = 0;
        OnPropertyChanged(nameof(ContentHeader));

        var account = CurrentAccount();
        Status = kind switch
        {
            "movies" => "Carregando filmes...",
            "series" => "Carregando séries...",
            "favorites" => "Carregando favoritos...",
            _ => "Carregando histórico..."
        };

        try
        {
            if (kind is "movies" or "series")
            {
                bool authenticated;
                try
                {
                    authenticated = await _xtream.ValidateAsync(account);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Status = "O servidor respondeu 404 ao autenticar esta lista. Se o mesmo servidor funcionava com outro usuário/senha, esta conta provavelmente está inválida, expirada, bloqueada ou não pertence a este servidor.";
                    return;
                }

                if (!authenticated)
                {
                    Status = "Usuário ou senha não foram autenticados pelo servidor. Verifique se esta conta está ativa e se pertence a este servidor.";
                    return;
                }
            }

            switch (kind)
            {
                case "movies":
                    if (_movies.Count == 0) _movies = await _xtream.GetMoviesAsync(account);
                    BuildCategories(_movies);
                    break;
                case "series":
                    if (_series.Count == 0) _series = await _xtream.GetSeriesAsync(account);
                    BuildCategories(_series);
                    break;
            }
            _settings.LastOpenedAccount = SelectedAccount;
            _settings.LastContentByAccount[SelectedAccount] = kind;
            await _store.SaveSettingsAsync(_settings);
            await ApplyFiltersAsync();
            Status = $"{ContentHeader} carregado.";
        }
        catch (Exception ex)
        {
            Status = $"Erro ao carregar {ContentHeader.ToLowerInvariant()}: {ex.Message}";
        }
        finally
        {
            IsContentLoading = false;
            OnPropertyChanged(nameof(ShowNoSearchResults));
        }
    }

    private List<MediaItem> BaseItems()
    {
        if (_currentView == "episodes") return _episodes;
        if (_currentView == "seasons") return _seasons;
        if (_currentView == "categories") return _categories;
        if (_currentView == "category")
        {
            var source = _contentKind == "series" ? _series : _movies;
            if (string.Equals(_selectedCategoryId, "__all__", StringComparison.OrdinalIgnoreCase))
                return source;
            return source.Where(i => string.Equals(i.CategoryId, _selectedCategoryId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return _contentKind switch
        {
            "series" => _series,
            "favorites" => FavoritesForCurrentAccount(),
            "history" => HistoryForCurrentAccount(),
            _ => _movies
        };
    }

    private void BuildCategories(List<MediaItem> source)
    {
        var allLabel = _contentKind == "series" ? "Todas as séries" : "Todos os filmes";
        var allCountLabel = _contentKind == "series" ? "séries" : "filmes";

        var categories = source
            .GroupBy(i => new { Id = i.CategoryId ?? "", Name = string.IsNullOrWhiteSpace(i.Group) ? "Sem categoria" : i.Group })
            .Select(g => new MediaItem
            {
                Type = "category",
                CategoryId = g.Key.Id,
                CategoryMediaKind = _contentKind,
                Name = g.Key.Name,
                Group = $"{g.Count()} conteúdo{(g.Count() == 1 ? "" : "s")}"
            })
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        categories.Insert(0, new MediaItem
        {
            Type = "category",
            CategoryId = "__all__",
            CategoryMediaKind = _contentKind,
            Name = allLabel,
            Group = $"{source.Count} {allCountLabel}"
        });

        _categories = categories;
    }

    private static bool IsXxxContent(MediaItem item)
    {
        static bool HasAdultMarker(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var normalized = TextTools.Normalize(value);
            var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

            // Marcadores XXX.
            if (normalized.Contains("[xxx]", StringComparison.OrdinalIgnoreCase)) return true;

            var tokens = normalized.Split(
                new[] { ' ', '-', '_', '.', '/', '\\', '[', ']', '(', ')', '{', '}', ':', ';', '|' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens.Any(t => t.Equals("xxx", StringComparison.OrdinalIgnoreCase))) return true;

            // Porno / Porn / Pornô / pornografia e variações comuns.
            if (tokens.Any(t => t.StartsWith("porn", StringComparison.OrdinalIgnoreCase))) return true;

            // Classificação +18 / 18+, inclusive quando há espaço no texto.
            if (compact.Contains("+18", StringComparison.OrdinalIgnoreCase)
                || compact.Contains("18+", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        return HasAdultMarker(item.Name)
            || HasAdultMarker(item.IptvName)
            || HasAdultMarker(item.Group)
            || HasAdultMarker(item.SeriesName)
            || HasAdultMarker(item.DirectSource)
            || HasAdultMarker(item.Logo);
    }

    private async Task SearchAsync()
    {
        _searchPerformed = !string.IsNullOrWhiteSpace(SearchText);

        // O campo de busca é global: ao pesquisar, garante que os dois catálogos
        // estejam carregados e procura em todos os filmes e todas as séries,
        // independentemente da categoria ou página atualmente aberta.
        if (_searchPerformed)
        {
            IsContentLoading = true;
            Status = "Pesquisando em todos os filmes e séries...";
            try
            {
                var account = CurrentAccount();
                if (_movies.Count == 0) _movies = await _xtream.GetMoviesAsync(account);
                if (_series.Count == 0) _series = await _xtream.GetSeriesAsync(account);
                CurrentPage = 1;
            }
            catch (Exception ex)
            {
                Status = $"Erro ao preparar busca global: {ex.Message}";
            }
            finally
            {
                IsContentLoading = false;
            }
        }

        await ApplyFiltersAsync();
        if (_searchPerformed) Status = $"Busca concluída: {_filtered.Count} resultado(s) em filmes e séries.";
        OnPropertyChanged(nameof(ContentHeader));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private async Task ApplyFiltersAsync()
    {
        var query = TextTools.Normalize(SearchText);
        // Com texto de busca, pesquisa no catálogo completo (filmes + séries),
        // sem limitar por categoria, página ou seção atualmente aberta.
        var baseItems = !string.IsNullOrWhiteSpace(query)
            ? _movies.Concat(_series).GroupBy(i => i.Identity).Select(g => g.First()).ToList()
            : BaseItems();
        var alpha = TextTools.Normalize(AlphaFilter);
        IEnumerable<MediaItem> q = baseItems;

        if (HideXxxContent) q = q.Where(i => !IsXxxContent(i));
        if (!string.IsNullOrWhiteSpace(alpha)) q = q.Where(i => i.SearchKey.StartsWith(alpha, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(i => tokens.All(t => i.SearchKey.Contains(t, StringComparison.Ordinal)));
        }

        // Mantém episódios e temporadas em ordem numérica crescente.
        // Ordenar pelo Name colocava E10 antes de E100/E101 por comparação textual.
        if (_currentView == "episodes" && string.IsNullOrWhiteSpace(query))
        {
            _filtered = q
                .OrderBy(i => i.Season)
                .ThenBy(i => i.EpisodeNumber)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else if (_currentView == "seasons" && string.IsNullOrWhiteSpace(query))
        {
            _filtered = q
                .OrderBy(i => i.Season)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else if (_currentView == "categories" && string.IsNullOrWhiteSpace(query))
        {
            _filtered = q
                .OrderBy(i => string.Equals(i.CategoryId, "__all__", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else
        {
            _filtered = q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        PageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
        CurrentPage = Math.Clamp(CurrentPage, 1, PageCount);
        Counter = !string.IsNullOrWhiteSpace(query)
            ? $"{_filtered.Count} resultado(s) em todos os filmes e séries — A→Z"
            : _currentView switch
        {
            "categories" => $"{_filtered.Count} categorias — A→Z",
            "category" when _contentKind == "series" => $"{_filtered.Count} séries — A→Z",
            "category" => $"{_filtered.Count} filmes MP4 — A→Z",
            "seasons" => $"{_filtered.Count} temporadas — ordem crescente",
            "episodes" => $"{_filtered.Count} episódios MP4 — ordem crescente",
            _ when _contentKind == "series" => $"{_filtered.Count} séries — A→Z",
            _ when _contentKind == "favorites" => $"{_filtered.Count} favoritos",
            _ when _contentKind == "history" => $"{_filtered.Count} no histórico",
            _ => $"{_filtered.Count} filmes MP4 — A→Z"
        };

        await RenderCurrentPageAsync();
    }

    private Task RenderCurrentPageAsync()
    {
        _metadataCts?.Cancel();
        _metadataCts = new CancellationTokenSource();
        var ct = _metadataCts.Token;

        var page = _filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
        VisibleItems.Clear();
        foreach (var item in page)
        {
            SyncSelectionAndFavorite(item);
            WireItem(item);
            SetDisplayText(item);
            VisibleItems.Add(item);
        }

        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(ContentHeader));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        _ = PrefetchPageAsync(page, ct);
        return Task.CompletedTask;
    }

    private async Task PrefetchPageAsync(List<MediaItem> page, CancellationToken ct)
    {
        var account = CurrentAccount();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = page.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (item.IsMovie || item.IsSeries)
                {
                    var cacheKey = item.Identity;
                    if (!_metadataCache.TryGetValue(cacheKey, out var payload))
                    {
                        payload = item.IsMovie
                            ? await _xtream.GetVodInfoAsync(account, item.StreamId, ct)
                            : await _xtream.GetSeriesInfoAsync(account, item.SeriesId, ct);
                        _metadataCache[cacheKey] = payload;
                    }
                    if (payload is not null)
                    {
                        if (item.IsMovie) XtreamClient.ApplyMovieMetadata(item, payload);
                        else XtreamClient.ApplySeriesMetadata(item, payload);
                        SetDisplayText(item);
                    }
                }
                if (item.CoverImage is null && !string.IsNullOrWhiteSpace(item.Logo))
                    item.CoverImage = await _images.GetAsync(item.Logo, ct);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private static void SetDisplayText(MediaItem item)
    {
        if (item.IsCategory)
        {
            item.MetaText = item.Group;
            item.PlotText = "";
            item.CreditsText = "";
        }
        else if (item.IsSeries)
        {
            var bits = new List<string>();
            if (item.SeasonCount is > 0) bits.Add(item.SeasonCount == 1 ? "1 temporada" : $"{item.SeasonCount} temporadas");
            else bits.Add("Temporadas: …");
            AddCommonBits(bits, item);
            var runtime = FormatDuration(item.EpisodeRunTime, 0, plainNumberIsMinutes: true);
            if (!string.IsNullOrWhiteSpace(runtime)) bits.Add($"{runtime}/ep");
            item.MetaText = string.Join("  •  ", bits);
        }
        else if (item.IsMovie)
        {
            var bits = new List<string> { "Filme" };
            AddCommonBits(bits, item);
            var runtime = FormatDuration(item.Duration, item.DurationSeconds);
            if (!string.IsNullOrWhiteSpace(runtime)) bits.Add(runtime);
            item.MetaText = string.Join("  •  ", bits);
        }
        else if (item.IsSeason)
        {
            item.MetaText = item.Group;
        }
        else
        {
            item.MetaText = string.Join("  •  ", new[] { item.Group, item.Duration, RatingText(item.Rating) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        item.PlotText = string.IsNullOrWhiteSpace(item.Plot) ? "" : "Sinopse: " + TextTools.Clip(item.Plot, 230);
        var credits = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Cast)) credits.Add("Elenco: " + TextTools.Clip(item.Cast, 110));
        if (!string.IsNullOrWhiteSpace(item.Director)) credits.Add("Direção: " + TextTools.Clip(item.Director, 70));
        item.CreditsText = string.Join("  •  ", credits);
    }

    private static void AddCommonBits(List<string> bits, MediaItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Year)) bits.Add(item.Year);
        if (!string.IsNullOrWhiteSpace(item.Genre)) bits.Add(TextTools.Clip(item.Genre, 45));
        var rating = RatingText(item.Rating);
        if (!string.IsNullOrWhiteSpace(rating)) bits.Add(rating);
    }

    private static string RatingText(string value) => double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? $"★ {n:0.0}" : "";
    private static string FormatDuration(string? raw, long durationSeconds = 0, bool plainNumberIsMinutes = true)
    {
        var totalMinutes = 0L;

        if (durationSeconds > 0)
        {
            totalMinutes = Math.Max(1, (long)Math.Round(durationSeconds / 60d));
        }
        else
        {
            var value = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            // Formatos comuns do Xtream: HH:MM:SS e MM:SS.
            // Só tenta TimeSpan quando há ':' para não interpretar "88" como 88 dias.
            if (value.Contains(':') && TimeSpan.TryParse(value, out var span) && span.TotalSeconds > 0)
            {
                totalMinutes = Math.Max(1, (long)Math.Round(span.TotalMinutes));
            }
            else
            {
                var lower = value.ToLowerInvariant();
                var hourMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)\s*h");
                var minuteMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)\s*(?:m|min)");
                if (hourMatch.Success || minuteMatch.Success)
                {
                    var hours = hourMatch.Success && long.TryParse(hourMatch.Groups[1].Value, out var h) ? h : 0;
                    var minutes = minuteMatch.Success && long.TryParse(minuteMatch.Groups[1].Value, out var m) ? m : 0;
                    totalMinutes = hours * 60 + minutes;
                }
                else
                {
                    var numberMatch = System.Text.RegularExpressions.Regex.Match(value, @"\d+");
                    if (numberMatch.Success && long.TryParse(numberMatch.Value, out var number) && number > 0)
                    {
                        // Duração de filme em número puro costuma vir em minutos.
                        // Valores muito altos geralmente estão em segundos.
                        totalMinutes = (!plainNumberIsMinutes || number > 300)
                            ? Math.Max(1, (long)Math.Round(number / 60d))
                            : number;
                    }
                }
            }
        }

        if (totalMinutes <= 0) return string.Empty;
        var hoursPart = totalMinutes / 60;
        var minutesPart = totalMinutes % 60;
        if (hoursPart > 0 && minutesPart > 0) return $"{hoursPart}h {minutesPart}min";
        if (hoursPart > 0) return $"{hoursPart}h";
        return $"{minutesPart}min";
    }

    private void WireItem(MediaItem item)
    {
        item.OpenCommand ??= new AsyncRelayCommand(() => OpenItemAsync(item));
        item.ToggleSelectCommand ??= new RelayCommand(() => ToggleSelect(item));
        item.FavoriteCommand ??= new AsyncRelayCommand(() => ToggleFavoriteAsync(item));
        item.DownloadCommand ??= new AsyncRelayCommand(() => DownloadSingleAsync(item));
        item.DownloadSeasonCommand ??= new AsyncRelayCommand(() => DownloadSeasonAsync(item));
    }

    private async Task OpenItemAsync(MediaItem item)
    {
        if (item.IsCategory)
        {
            await SaveStateAsync();
            _selectedCategoryId = item.CategoryId;
            _selectedCategoryName = item.Name;
            _currentView = "category";
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            OnPropertyChanged(nameof(ContentHeader));
            await ApplyFiltersAsync();
            Status = $"Categoria \"{item.Name}\" aberta.";
        }
        else if (item.IsSeries)
        {
            await SaveStateAsync();
            Status = $"Abrindo temporadas de \"{item.Name}\"...";
            var payload = await GetSeriesPayloadAsync(item);
            _seasons = await _xtream.BuildSeasonsAsync(CurrentAccount(), item, payload);
            _currentSeries = item;
            _currentSeason = null;
            _currentView = "seasons";
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            OnPropertyChanged(nameof(ContentHeader));
            await ApplyFiltersAsync();
            Status = $"{_seasons.Count} temporadas.";
        }
        else if (item.IsSeason)
        {
            await SaveStateAsync();
            _currentSeason = item;
            _episodes = item.Episodes;
            _currentView = "episodes";
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            OnPropertyChanged(nameof(ContentHeader));
            await ApplyFiltersAsync();
            Status = $"{item.Name}: {_episodes.Count} episódios.";
        }
    }

    private async Task<JsonObject?> GetSeriesPayloadAsync(MediaItem series)
    {
        if (_metadataCache.TryGetValue(series.Identity, out var cached) && cached is not null) return cached;
        var payload = await _xtream.GetSeriesInfoAsync(CurrentAccount(), series.SeriesId);
        _metadataCache[series.Identity] = payload;
        return payload;
    }

    private async Task BackAsync()
    {
        await SaveStateAsync();
        if (_currentView == "episodes")
        {
            _currentView = "seasons";
            _currentSeason = null;
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            await ApplyFiltersAsync();
        }
        else if (_currentView == "seasons")
        {
            _currentView = "category";
            _currentSeries = null;
            _currentSeason = null;
            _contentKind = "series";
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            await ApplyFiltersAsync();
        }
        else if (_currentView == "category")
        {
            _currentView = "categories";
            _selectedCategoryId = "";
            _selectedCategoryName = "";
            SearchText = "";
            AlphaFilter = "";
            CurrentPage = 1;
            ScrollOffset = 0;
            await ApplyFiltersAsync();
        }
        else
        {
            Status = "Você já está nas categorias.";
        }
        OnPropertyChanged(nameof(ContentHeader));
    }

    private async Task PrevPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        ScrollOffset = 0;
        await RenderCurrentPageAsync();
        ScrollRestoreRequested?.Invoke(0);
    }

    private async Task NextPageAsync()
    {
        if (CurrentPage >= PageCount) return;
        CurrentPage++;
        ScrollOffset = 0;
        await RenderCurrentPageAsync();
        ScrollRestoreRequested?.Invoke(0);
    }

    private async Task SetAlphaAsync(string raw)
    {
        AlphaFilter = raw == "•" ? "" : raw.ToUpperInvariant();
        CurrentPage = 1;
        ScrollOffset = 0;
        await ApplyFiltersAsync();
    }

    private void ToggleSelect(MediaItem item)
    {
        item.IsSelected = !item.IsSelected;
        if (item.IsSelected) _selection[item.Identity] = item;
        else _selection.Remove(item.Identity);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadSelectedText));
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        Status = item.IsSeries && item.IsSelected
            ? $"Série completa selecionada: {item.Name} — todas as temporadas e episódios serão baixados."
            : item.IsSelected ? $"Selecionado: {item.Name}" : $"Removido da seleção: {item.Name}";
    }

    private void SelectVisible()
    {
        foreach (var item in VisibleItems.Where(i => i.IsSelectable))
        {
            item.IsSelected = true;
            _selection[item.Identity] = item;
        }
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadSelectedText));
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        Status = $"{VisibleItems.Count(i => i.IsSelectable)} item(ns) desta página marcado(s). Total na fila: {_selection.Count}.";
    }

    private void ClearSelection()
    {
        foreach (var item in _selection.Values) item.IsSelected = false;
        _selection.Clear();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DownloadSelectedText));
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        Status = "Seleção limpa.";
    }

    private void SyncSelectionAndFavorite(MediaItem item)
    {
        item.IsSelected = _selection.ContainsKey(item.Identity);
        item.IsFavorite = FavoritesForCurrentAccount().Any(f => f.Identity == item.Identity);
    }

    private async Task ToggleFavoriteAsync(MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(SelectedAccount) || !item.IsFavoriteCapable) return;
        if (!_favorites.TryGetValue(SelectedAccount, out var bucket)) _favorites[SelectedAccount] = bucket = [];
        var existing = bucket.FindIndex(x => x.Identity == item.Identity);
        if (existing >= 0)
        {
            bucket.RemoveAt(existing);
            item.IsFavorite = false;
            Status = $"Removido dos favoritos: {item.Name}";
        }
        else
        {
            bucket.Add(CopyForStorage(item));
            item.IsFavorite = true;
            Status = $"Adicionado aos favoritos: {item.Name}";
        }
        await _store.SaveFavoritesAsync(_favorites);
        if (_contentKind == "favorites") await ApplyFiltersAsync();
    }

    private List<MediaItem> FavoritesForCurrentAccount() =>
        _favorites.TryGetValue(SelectedAccount, out var items) ? items : [];

    private List<MediaItem> HistoryForCurrentAccount() =>
        _history.TryGetValue(SelectedAccount, out var items) ? items : [];

    private static MediaItem CopyForStorage(MediaItem i) => new()
    {
        Type = i.Type,
        Name = i.Name,
        IptvName = i.IptvName,
        Group = i.Group,
        CategoryId = i.CategoryId,
        Logo = i.Logo,
        StreamId = i.StreamId,
        SeriesId = i.SeriesId,
        EpisodeId = i.EpisodeId,
        SeriesName = i.SeriesName,
        Season = i.Season,
        EpisodeNumber = i.EpisodeNumber,
        ContainerExtension = i.ContainerExtension,
        DirectSource = i.DirectSource,
        Year = i.Year,
        Genre = i.Genre,
        Plot = i.Plot,
        Rating = i.Rating,
        Cast = i.Cast,
        Director = i.Director,
        Duration = i.Duration,
        DurationSeconds = i.DurationSeconds,
        EpisodeRunTime = i.EpisodeRunTime,
        ReleaseDate = i.ReleaseDate,
        SeasonCount = i.SeasonCount
    };

    private async Task DownloadSelectedAsync()
    {
        if (_selection.Count == 0 || IsDownloading) return;
        Status = "Preparando fila de downloads...";
        try
        {
            var selectedSnapshot = _selection.Keys.ToList();
            var queue = await ExpandSelectionAsync(_selection.Values.ToList());
            await RunDownloadQueueAsync(queue);
            if (RemoveCompletedFromSelection)
            {
                foreach (var key in selectedSnapshot) _selection.Remove(key);
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(DownloadSelectedText));
                (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                Status = $"Fila concluída: {queue.Count} arquivo(s). Seleção removida.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Erro ao preparar downloads: {ex.Message}";
        }
    }

    private async Task DownloadSingleAsync(MediaItem item)
    {
        if (!item.IsDownloadable || IsDownloading) return;
        await RunDownloadQueueAsync([item]);
    }

    private async Task DownloadSeasonAsync(MediaItem season)
    {
        if (!season.IsSeason || season.Episodes.Count == 0 || IsDownloading) return;
        await RunDownloadQueueAsync(season.Episodes);
    }

    private async Task<List<MediaItem>> ExpandSelectionAsync(List<MediaItem> selected)
    {
        var output = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
        foreach (var item in selected)
        {
            if (item.IsSeries)
            {
                Status = $"Preparando série completa: {item.Name}...";
                var payload = await GetSeriesPayloadAsync(item);
                var seasons = await _xtream.BuildSeasonsAsync(CurrentAccount(), item, payload);
                foreach (var ep in seasons.SelectMany(s => s.Episodes)) output[ep.Identity] = ep;
            }
            else if (item.IsMovie || item.IsEpisode)
            {
                output[item.Identity] = item;
            }
        }
        Status = $"Fila preparada: {output.Count} arquivo(s).";
        return output.Values.ToList();
    }

    private async Task RunDownloadQueueAsync(IReadOnlyList<MediaItem> queue)
    {
        if (queue.Count == 0) return;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        _pauseGate.Resume();
        Progress = 0;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Status = p.Status;
                Progress = p.OverallFraction;
                OverallText = $"Total: {p.OverallFraction * 100:0.0}% concluído • Falta: {(1 - p.OverallFraction) * 100:0.0}%";
                SpeedText = p.BytesPerSecond > 0 ? $"Velocidade: {FormatSpeed(p.BytesPerSecond)}" : "Velocidade: --";
                SizeText = p.FileBytesTotal > 0
                    ? $"Arquivo: {FormatBytes(p.FileBytesDownloaded)} / {FormatBytes(p.FileBytesTotal)}"
                    : p.FileBytesDownloaded > 0
                        ? $"Arquivo: {FormatBytes(p.FileBytesDownloaded)} / total desconhecido"
                        : "Arquivo: -- / --";
                RemainingTimeText = EstimateRemainingTime(p.FileBytesDownloaded, p.FileBytesTotal, p.BytesPerSecond);
            });

            await _downloader.DownloadBatchAsync(
                queue,
                CurrentAccount(),
                DownloadRoot,
                progress,
                AddHistoryAsync,
                _pauseGate,
                _downloadCts.Token);

            Status = $"Fila concluída: {queue.Count} arquivo(s).";
            Progress = 1;
            if (queue.Count > 0) SizeText = "Arquivo concluído";
            RemainingTimeText = "Tempo restante: concluído";
            if (ShutdownAfterBatch) TryShutdownComputer();
        }
        catch (OperationCanceledException)
        {
            Status = "Downloads cancelados.";
        }
        catch (Exception ex)
        {
            Status = $"Erro no download: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _pauseGate.Resume();
            SpeedText = "Velocidade: --";
            if (Progress < 0.9999) RemainingTimeText = "Tempo restante: --";
        }
    }

    private async Task AddHistoryAsync(MediaItem item)
    {
        if (!_history.TryGetValue(SelectedAccount, out var bucket)) _history[SelectedAccount] = bucket = [];
        bucket.RemoveAll(x => x.Identity == item.Identity);
        bucket.Insert(0, CopyForStorage(item));
        if (bucket.Count > 500) bucket.RemoveRange(500, bucket.Count - 500);
        await _store.SaveHistoryAsync(_history);
    }

    private void TogglePause()
    {
        if (!IsDownloading) return;
        if (_pauseGate.IsPaused)
        {
            _pauseGate.Resume();
            Status = "Download retomado.";
        }
        else
        {
            _pauseGate.Pause();
            Status = "Download pausado.";
        }
        OnPropertyChanged(nameof(PauseText));
    }

    private void CancelDownload() => _downloadCts?.Cancel();

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.00} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }


    private static string EstimateRemainingTime(long downloaded, long total, double bytesPerSecond)
    {
        if (total <= 0 || downloaded < 0 || downloaded >= total)
            return downloaded >= total && total > 0 ? "Tempo restante: concluído" : "Tempo restante: --";
        if (bytesPerSecond <= 1) return "Tempo restante: calculando...";

        var seconds = (total - downloaded) / bytesPerSecond;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "Tempo restante: --";

        var eta = TimeSpan.FromSeconds(seconds);
        if (eta.TotalHours >= 1)
            return $"Tempo restante: {(int)eta.TotalHours}h {eta.Minutes:00}min";
        if (eta.TotalMinutes >= 1)
            return $"Tempo restante: {(int)eta.TotalMinutes}min {eta.Seconds:00}s";
        return $"Tempo restante: {Math.Max(1, eta.Seconds)}s";
    }

    private static string FormatSpeed(double bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024:0.0} MB/s";
        if (bytes >= 1024) return $"{bytes / 1024:0} KB/s";
        return $"{bytes:0} B/s";
    }

    private static void TryShutdownComputer()
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true });
            else Process.Start(new ProcessStartInfo("systemctl", "poweroff -i") { CreateNoWindow = true });
        }
        catch { }
    }

    private void SaveNavigationSnapshot()
    {
        if (string.IsNullOrWhiteSpace(SelectedAccount)) return;
        var nav = new NavigationState
        {
            View = _currentView,
            Kind = string.IsNullOrWhiteSpace(_contentKind) ? "movies" : _contentKind,
            SeriesId = _currentSeries?.SeriesId ?? "",
            SeriesName = _currentSeries?.Name ?? "",
            Season = _currentSeason?.Season ?? 0,
            CategoryId = _selectedCategoryId,
            CategoryName = _selectedCategoryName,
            Query = SearchText,
            AlphaFilter = AlphaFilter,
            Page = CurrentPage,
            ScrollOffset = ScrollOffset
        };
        _settings.LastOpenedAccount = SelectedAccount;
        if (!string.IsNullOrWhiteSpace(_contentKind)) _settings.LastContentByAccount[SelectedAccount] = _contentKind;
        _settings.LastNavigationByAccount[SelectedAccount] = nav;
    }

    private async Task RestoreNavigationAsync(NavigationState nav)
    {
        if (nav.Kind is "movies" or "series")
        {
            var source = nav.Kind == "series" ? _series : _movies;
            BuildCategories(source);
            _currentView = nav.View is "category" or "seasons" or "episodes" ? nav.View : "categories";
            _selectedCategoryId = nav.CategoryId ?? "";
            _selectedCategoryName = nav.CategoryName ?? "";
        }

        if (nav.Kind == "series" && nav.View is "seasons" or "episodes" && !string.IsNullOrWhiteSpace(nav.SeriesId))
        {
            var series = _series.FirstOrDefault(x => x.SeriesId == nav.SeriesId);
            if (series is not null)
            {
                if (string.IsNullOrWhiteSpace(_selectedCategoryId))
                {
                    _selectedCategoryId = series.CategoryId;
                    _selectedCategoryName = series.Group;
                }
                var payload = await GetSeriesPayloadAsync(series);
                _seasons = await _xtream.BuildSeasonsAsync(CurrentAccount(), series, payload);
                _currentSeries = series;
                _currentView = "seasons";
                if (nav.View == "episodes")
                {
                    var season = _seasons.FirstOrDefault(s => s.Season == nav.Season);
                    if (season is not null)
                    {
                        _currentSeason = season;
                        _episodes = season.Episodes;
                        _currentView = "episodes";
                    }
                }
            }
        }

        SearchText = nav.Query ?? "";
        AlphaFilter = nav.AlphaFilter ?? "";
        CurrentPage = Math.Max(1, nav.Page);
        ScrollOffset = Math.Max(0, nav.ScrollOffset);
        OnPropertyChanged(nameof(ContentHeader));
        await ApplyFiltersAsync();
        CurrentPage = Math.Clamp(nav.Page, 1, PageCount);
        await RenderCurrentPageAsync();
        ScrollRestoreRequested?.Invoke(ScrollOffset);
    }
}
