using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;

namespace IPTVDownloader.Services;

public sealed partial class DownloadService
{

    private readonly XtreamClient _xtream;
    private readonly HttpClient _downloadHttp;

    public DownloadService(XtreamClient xtream)
    {
        _xtream = xtream;

        // Cliente exclusivo para os arquivos grandes. Ele não divide o pool de
        // conexões com capas, catálogo ou metadados e imita o comportamento da
        // versão Python/requests, que neste tipo de servidor costuma entregar
        // uma taxa maior e mais estável.
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = false
        };

        _downloadHttp = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        _downloadHttp.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/152.0 Safari/537.36");
        _downloadHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _downloadHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        _downloadHttp.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task DownloadBatchAsync(IReadOnlyList<MediaItem> items, XtreamAccount account,
        string downloadRoot, IProgress<DownloadProgress> progress,
        Func<MediaItem, Task>? completedCallback = null, AsyncPauseGate? pauseGate = null,
        CancellationToken ct = default, int connections = 1)
    {
        foreach (var item in items)
        {
            var output = OutputPath(downloadRoot, item);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await TransferAsync(ResolveUrl(item, account), output, progress, pauseGate, ct, connections);
            if (completedCallback != null) await completedCallback(item);
        }
    }

    public static string OutputPath(string root, MediaItem item)
    {
        var ext = NormalizeExtension(item.ContainerExtension);

        if (item.IsMovie)
            return Path.Combine(root, "filmes", TextTools.SafeFileName(item.Name) + "." + ext);

        if (item.IsEpisode)
            return Path.Combine(
                root,
                "series",
                TextTools.SafeFileName(item.SeriesName),
                $"Temporada {item.Season:00}",
                TextTools.SafeFileName(item.Name) + "." + ext);

        return Path.Combine(root, TextTools.SafeFileName(item.Name) + "." + ext);
    }

    private static string ResolveUrl(MediaItem item, XtreamAccount account)
    {
        if (!string.IsNullOrWhiteSpace(item.DirectSource)) return item.DirectSource;

        // Não força mais .mp4. Muitos painéis Xtream entregam filmes/episódios
        // em .mkv, .ts ou outra extensão informada pelo próprio catálogo; usar
        // .mp4 nesses casos gera 404 mesmo com usuário e senha corretos.
        var ext = NormalizeExtension(item.ContainerExtension);
        return item.IsMovie
            ? XtreamClient.MovieUrl(account, item.StreamId, ext)
            : XtreamClient.EpisodeUrl(account, item.EpisodeId, ext);
    }

    private static string NormalizeExtension(string? extension)
    {
        var ext = (extension ?? "").Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(ext)) return "mp4";

        // Mantém apenas caracteres seguros de extensão.
        ext = new string(ext.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        return string.IsNullOrWhiteSpace(ext) ? "mp4" : ext.ToLowerInvariant();
    }

    public sealed class DownloadProblem : IOException
    {
        public DownloadProblem(string message) : base(message) { }
    }
    private sealed record ResumeInfo(string? ETag, DateTimeOffset? Modified, long Total);
    private sealed record Receipt(long Length, string Sha256);
    private static void AtomicJson<T>(string path, T value)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write))
        {
            System.Text.Json.JsonSerializer.Serialize(file, value);
            file.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }
    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException) { return default; }
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
    }
    public static void RestartFile(string output)
    {
        // Preserve previous files rather than erase a user's media.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var suffix in new[] { "", ".part", ".merge", ".resume.json", ".verified.json" })
            if (File.Exists(output + suffix)) File.Move(output + suffix, output + suffix + ".backup-" + stamp);
        if (Directory.Exists(output + ".segments")) Directory.Move(output + ".segments", output + ".segments.backup-" + stamp);
    }
    private async Task TransferAsync(string url, string output, IProgress<DownloadProgress> progress,
        AsyncPauseGate? pause, CancellationToken ct, int connections)
    {
        var part = output + ".part";
        var metadata = output + ".resume.json";
        var receiptPath = output + ".verified.json";
        if (File.Exists(output))
        {
            var receipt = ReadJson<Receipt>(receiptPath);
            if (receipt != null && receipt.Length > 0 && new FileInfo(output).Length == receipt.Length
                && await HashAsync(output, ct) == receipt.Sha256)
            {
                File.Delete(receiptPath);
                return;
            }
            throw new DownloadProblem("Arquivo existente sem confirmação de integridade. Reinicie o download; o arquivo antigo será preservado como backup.");
        }
        if (!File.Exists(part) && (connections > 1 || Directory.Exists(output + ".segments")))
            if (await TrySegmentedAsync(url, output, connections, progress, pause, ct)) return;
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        var info = ReadJson<ResumeInfo>(metadata);
        if (offset > 0 && (info == null || (info.ETag == null && info.Modified == null)))
            throw new DownloadProblem("O servidor não forneceu identificação segura para retomar. Use Reiniciar download.");
        if (offset > 0 && info!.Total > 0 && offset == info.Total)
        {
            await FinishAsync();
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = info!.ETag != null
                ? new RangeConditionHeaderValue(new EntityTagHeaderValue(info.ETag))
                : new RangeConditionHeaderValue(info.Modified!.Value);
        }
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headersTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            throw new DownloadProblem("Servidor recusou a retomada ou alterou o arquivo. Use Reiniciar download.");
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            throw new DownloadProblem($"Servidor respondeu HTTP {(int)response.StatusCode}. Tente novamente mais tarde.");
        var range = response.Content.Headers.ContentRange;
        long total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range == null || range.Unit != "bytes" || range.From != offset || range.Length == null
                || range.To != range.Length - 1 || (response.Content.Headers.ContentLength is long size && size != range.Length - offset))
                throw new DownloadProblem("Servidor enviou uma faixa de dados inconsistente. Arquivo parcial preservado.");
            total = range.Length.Value;
        }
        else total = response.Content.Headers.ContentLength ?? 0;
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        var modified = response.Content.Headers.LastModified;
        if (offset > 0 && (total != info!.Total || (info.ETag != null && etag != info.ETag)
            || (info.ETag == null && modified != info.Modified)))
            throw new DownloadProblem("O arquivo no servidor mudou. Use Reiniciar download.");
        info = new ResumeInfo(etag, modified, total);
        AtomicJson(metadata, info);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 131072, FileOptions.Asynchronous))
        {
            var buffer = new byte[131072];
            var watch = Stopwatch.StartNew();
            long downloaded = offset, last = offset;
            var lastTime = TimeSpan.Zero;
            while (true)
            {
                if (pause != null) await pause.WaitAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (total > 0 && downloaded + read > total)
                    throw new DownloadProblem("Servidor enviou dados além do tamanho esperado. Reinicie o download.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (watch.Elapsed - lastTime >= TimeSpan.FromSeconds(1))
                {
                    file.Flush(true);
                    var speed = (downloaded - last) / (watch.Elapsed - lastTime).TotalSeconds;
                    progress.Report(new DownloadProgress("Baixando", total > 0 ? downloaded / (double)total : 0,
                        0, speed, 0, 1, downloaded, total));
                    last = downloaded; lastTime = watch.Elapsed;
                }
            }
            file.Flush(true);
            if (total <= 0) throw new DownloadProblem("Transferência encerrada, mas o servidor não informou o tamanho. Conclusão não confirmada; arquivo parcial preservado.");
            if (downloaded != total) throw new DownloadProblem("Arquivo incompleto. Use Tentar novamente para retomar.");
        }
        await FinishAsync();

        async Task FinishAsync()
        {
            ct.ThrowIfCancellationRequested();
            var length = new FileInfo(part).Length;
            var hash = await HashAsync(part, ct);
            // Receipt is durable before rename; recovery can finish a verified partial.
            AtomicJson(receiptPath, new Receipt(length, hash));
            File.Move(part, output);
            if (File.Exists(metadata)) File.Delete(metadata);
            // A confirmação de integridade já terminou; não deixar JSON ao lado do vídeo.
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            progress.Report(new DownloadProgress("Concluído", 1, 1, 0, 1, 1, length, length));
        }
    }
}
