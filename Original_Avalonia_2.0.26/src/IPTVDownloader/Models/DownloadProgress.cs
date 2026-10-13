namespace IPTVDownloader.Models;

public sealed record DownloadProgress(
    string Status,
    double FileFraction,
    double OverallFraction,
    double BytesPerSecond,
    int Completed,
    int Total,
    long FileBytesDownloaded = 0,
    long FileBytesTotal = 0);
