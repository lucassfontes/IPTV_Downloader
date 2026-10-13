using System.Text.Json.Serialization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using IPTVDownloader.Infrastructure;

namespace IPTVDownloader.Models;

public sealed class MediaItem : ObservableObject
{
    private bool _isSelected;
    private bool _isFavorite;
    private string _status = "";
    private string _metaText = "";
    private string _plotText = "";
    private string _creditsText = "";
    private Bitmap? _coverImage;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("iptv_name")]
    public string IptvName { get; set; } = "";

    [JsonPropertyName("group")]
    public string Group { get; set; } = "";

    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = "";

    [JsonPropertyName("logo")]
    public string Logo { get; set; } = "";

    [JsonPropertyName("stream_id")]
    public string StreamId { get; set; } = "";

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = "";

    [JsonPropertyName("episode_id")]
    public string EpisodeId { get; set; } = "";

    [JsonPropertyName("series_name")]
    public string SeriesName { get; set; } = "";

    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("episode_num")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("container_extension")]
    public string ContainerExtension { get; set; } = "mp4";

    [JsonPropertyName("direct_source")]
    public string DirectSource { get; set; } = "";

    [JsonPropertyName("year")]
    public string Year { get; set; } = "";

    [JsonPropertyName("genre")]
    public string Genre { get; set; } = "";

    [JsonPropertyName("plot")]
    public string Plot { get; set; } = "";

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = "";

    [JsonPropertyName("cast")]
    public string Cast { get; set; } = "";

    [JsonPropertyName("director")]
    public string Director { get; set; } = "";

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = "";

    [JsonPropertyName("duration_secs")]
    public long DurationSeconds { get; set; }

    [JsonPropertyName("episode_run_time")]
    public string EpisodeRunTime { get; set; } = "";

    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = "";

    [JsonPropertyName("season_count")]
    public int? SeasonCount { get; set; }

    [JsonPropertyName("episodes")]
    public List<MediaItem> Episodes { get; set; } = [];

    [JsonIgnore]
    public string Identity => Type switch
    {
        "movie" => $"movie:{StreamId}",
        "series" => $"series:{SeriesId}",
        "episode" => $"episode:{EpisodeId}",
        "season" => $"season:{SeriesId}:{Season}",
        "category" => $"category:{CategoryId}:{Name}",
        _ => $"{Type}:{Name}"
    };

    [JsonIgnore]
    public string SearchKey => TextTools.Normalize(Name);

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectText));
                OnPropertyChanged(nameof(SelectButtonColor));
            }
        }
    }

    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
                OnPropertyChanged(nameof(FavoriteText));
        }
    }

    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    [JsonIgnore]
    public string MetaText { get => _metaText; set => SetProperty(ref _metaText, value); }

    [JsonIgnore]
    public string PlotText
    {
        get => _plotText;
        set
        {
            if (SetProperty(ref _plotText, value))
                OnPropertyChanged(nameof(HasPlot));
        }
    }

    [JsonIgnore]
    public string CreditsText
    {
        get => _creditsText;
        set
        {
            if (SetProperty(ref _creditsText, value))
                OnPropertyChanged(nameof(HasCredits));
        }
    }

    [JsonIgnore]
    public Bitmap? CoverImage { get => _coverImage; set => SetProperty(ref _coverImage, value); }

    [JsonIgnore] public string CategoryMediaKind { get; set; } = "";

    [JsonIgnore]
    public Geometry? CategoryIconGeometry
    {
        get
        {
            if (!IsCategory) return null;

            // Vetores próprios: não dependem de emoji/fonte do sistema.
            if (string.Equals(CategoryId, "__all__", StringComparison.OrdinalIgnoreCase))
            {
                // Grade / catálogo
                return Geometry.Parse("M3,3 L10,3 L10,10 L3,10 Z M14,3 L21,3 L21,10 L14,10 Z M3,14 L10,14 L10,21 L3,21 Z M14,14 L21,14 L21,21 L14,21 Z");
            }

            if (string.Equals(CategoryMediaKind, "series", StringComparison.OrdinalIgnoreCase))
            {
                // TV
                return Geometry.Parse("M3,5 L21,5 L21,17 L3,17 Z M8,21 L16,21 M12,17 L12,21 M8,2 L12,5 L16,2");
            }

            // Claquete / filme
            return Geometry.Parse("M3,7 L21,7 L21,20 L3,20 Z M3,7 L5,3 L9,3 L7,7 M9,7 L11,3 L15,3 L13,7 M15,7 L17,3 L21,3 L19,7");
        }
    }

    [JsonIgnore] public bool IsCategory => Type == "category";
    [JsonIgnore] public bool IsSeries => Type == "series";
    [JsonIgnore] public bool IsSeason => Type == "season";
    [JsonIgnore] public bool IsEpisode => Type == "episode";
    [JsonIgnore] public bool IsMovie => Type == "movie";
    [JsonIgnore] public bool IsDownloadable => IsMovie || IsEpisode;
    [JsonIgnore] public bool IsSelectable => IsMovie || IsSeries || IsEpisode;
    [JsonIgnore] public bool IsFavoriteCapable => IsMovie || IsSeries;
    [JsonIgnore] public bool CanOpenFromCard => IsCategory || IsSeries || IsSeason;
    [JsonIgnore] public bool HasPlot => !string.IsNullOrWhiteSpace(PlotText);
    [JsonIgnore] public bool HasCredits => !string.IsNullOrWhiteSpace(CreditsText);
    [JsonIgnore] public bool HasStatus => !string.IsNullOrWhiteSpace(Status) && !Status.Equals("Pronto", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string FavoriteText => IsFavorite ? "★ Favorito" : "☆ Favoritar";
    [JsonIgnore] public string SelectText => IsSeries
        ? (IsSelected ? "☑ Série completa" : "☐ Selecionar série")
        : (IsSelected ? "☑ Selecionado" : "☐ Selecionar");
    [JsonIgnore] public string SelectButtonColor => IsSelected ? "#176b3a" : "#26374b";
    [JsonIgnore] public string OpenText => IsCategory ? "Abrir categoria" : IsSeries ? "Abrir temporadas" : IsSeason ? "Abrir temporada" : "Abrir";

    [JsonIgnore] public ICommand? OpenCommand { get; set; }
    [JsonIgnore] public ICommand? ToggleSelectCommand { get; set; }
    [JsonIgnore] public ICommand? FavoriteCommand { get; set; }
    [JsonIgnore] public ICommand? DownloadCommand { get; set; }
    [JsonIgnore] public ICommand? DownloadSeasonCommand { get; set; }
}
