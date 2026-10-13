using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace IPTVDownloader.Services;

public sealed class ImageCacheService
{
    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly Dictionary<string, Bitmap> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(6, 6);

    public ImageCacheService(HttpClient http, AppPaths paths)
    {
        _http = http;
        _paths = paths;
    }

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_memory.TryGetValue(url, out var cached)) return cached;

        var path = CachePath(url);
        try
        {
            if (!File.Exists(path))
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (!File.Exists(path))
                    {
                        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
                        await input.CopyToAsync(output, ct);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            await using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            _memory[url] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string CachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_paths.LogoCache, hash + ".img");
    }
}
