using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;

namespace IPTVDownloader.Services;

public sealed class DownloadService
{
    private const int BufferSize = 1024 * 1024; // 1 MiB, igual ao bloco usado pela versão Python
    private const int ParallelSegments = 4;
    private const long MinSegmentedSize = 32L * 1024 * 1024; // 32 MiB

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
            MaxConnectionsPerServer = 8,
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

    public async Task DownloadBatchAsync(
        IReadOnlyList<MediaItem> items,
        XtreamAccount account,
        string downloadRoot,
        IProgress<DownloadProgress> progress,
        Func<MediaItem, Task>? completedCallback = null,
        AsyncPauseGate? pauseGate = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(downloadRoot);
        var completed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            item.Status = "Preparando...";
            var output = OutputPath(downloadRoot, item);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            if (File.Exists(output) && new FileInfo(output).Length > 0)
            {
                item.Status = "Já existe";
                completed++;
                progress.Report(new DownloadProgress(
                    $"Já existe: {item.Name}",
                    1,
                    completed / (double)items.Count,
                    0,
                    completed,
                    items.Count,
                    new FileInfo(output).Length,
                    new FileInfo(output).Length));
                continue;
            }

            var url = ResolveUrl(item, account);
            item.Status = "Baixando";
            var fileProgress = new Progress<(double fraction, double speed, bool turbo, long downloaded, long total)>(p =>
            {
                var overall = (i + p.fraction) / Math.Max(1.0, items.Count);
                var mode = p.turbo ? "Turbo • " : string.Empty;
                progress.Report(new DownloadProgress(
                    $"{mode}Baixando: {item.Name}",
                    p.fraction,
                    overall,
                    p.speed,
                    completed,
                    items.Count,
                    p.downloaded,
                    p.total));
            });

            await DownloadFileAsync(url, output, fileProgress, pauseGate, ct);

            item.Status = "Concluído";
            completed++;
            progress.Report(new DownloadProgress(
                $"Concluído: {item.Name}",
                1,
                completed / (double)items.Count,
                0,
                completed,
                items.Count,
                FileLengthSafe(output),
                FileLengthSafe(output)));

            if (completedCallback is not null) await completedCallback(item);
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

    private async Task DownloadFileAsync(
        string url,
        string output,
        IProgress<(double fraction, double speed, bool turbo, long downloaded, long total)> progress,
        AsyncPauseGate? pauseGate,
        CancellationToken ct)
    {
        var tempPath = output + ".part";
        SafeDelete(tempPath);

        // No Linux, prioriza o curl nativo para a transferência pesada. Ele usa
        // a pilha de rede nativa do sistema e, em vários servidores Xtream,
        // alcança a mesma taxa ou uma taxa maior que requests/Python.
        // A URL (que pode conter usuário/senha) é enviada pelo stdin do curl,
        // para não ficar exposta na linha de comando/process list.
        if (OperatingSystem.IsLinux() && TryFindCurl(out var curlPath))
        {
            try
            {
                await DownloadWithCurlAsync(
                    curlPath,
                    url,
                    tempPath,
                    progress,
                    pauseGate,
                    ct);

                FinalizeDownload(tempPath, output);
                return;
            }
            catch (OperationCanceledException)
            {
                SafeDelete(tempPath);
                throw;
            }
            catch
            {
                // Se o curl falhar por qualquer incompatibilidade específica do
                // provedor, limpa o parcial e volta automaticamente ao motor .NET.
                SafeDelete(tempPath);
            }
        }

        await DownloadSingleStreamAsync(url, tempPath, progress, pauseGate, ct);
        FinalizeDownload(tempPath, output);
    }

    private async Task DownloadWithCurlAsync(
        string curlPath,
        string url,
        string tempPath,
        IProgress<(double fraction, double speed, bool turbo, long downloaded, long total)> progress,
        AsyncPauseGate? pauseGate,
        CancellationToken ct)
    {
        var total = await TryGetContentLengthAsync(url, ct);

        var psi = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Opções de transferência. A URL é passada via --config - (stdin) para
        // não expor credenciais do Xtream em /proc/<pid>/cmdline.
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("--location");
        psi.ArgumentList.Add("--fail");
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--show-error");
        psi.ArgumentList.Add("--http1.1");
        psi.ArgumentList.Add("--connect-timeout");
        psi.ArgumentList.Add("15");
        psi.ArgumentList.Add("--retry");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("--retry-delay");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(tempPath);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new IOException("Não foi possível iniciar o motor de download nativo.");

        // Consumir stdout/stderr evita bloqueio caso o processo produza saída.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var config = $"url = {CurlQuote(url)}\n" +
                     $"header = {CurlQuote("User-Agent: Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/152.0 Safari/537.36")}\n" +
                     $"header = {CurlQuote("Accept: */*")}\n" +
                     $"header = {CurlQuote("Accept-Encoding: identity")}\n";

        await process.StandardInput.WriteAsync(config.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        long lastBytes = 0;
        var sw = Stopwatch.StartNew();
        var lastTime = TimeSpan.Zero;
        var nativePaused = false;

        try
        {
            while (!process.HasExited)
            {
                ct.ThrowIfCancellationRequested();

                if (pauseGate is not null)
                {
                    if (pauseGate.IsPaused && !nativePaused)
                    {
                        SendUnixSignal(process.Id, "STOP");
                        nativePaused = true;
                    }
                    else if (!pauseGate.IsPaused && nativePaused)
                    {
                        SendUnixSignal(process.Id, "CONT");
                        nativePaused = false;
                        lastBytes = FileLengthSafe(tempPath);
                        lastTime = sw.Elapsed;
                    }
                }

                var now = sw.Elapsed;
                if (!nativePaused && now - lastTime >= TimeSpan.FromMilliseconds(250))
                {
                    var current = FileLengthSafe(tempPath);
                    var delta = current - lastBytes;
                    var seconds = (now - lastTime).TotalSeconds;
                    var speed = seconds > 0 && delta >= 0 ? delta / seconds : 0;
                    var fraction = total > 0 ? Math.Clamp(current / (double)total, 0, 1) : 0;
                    progress.Report((fraction, speed, true, current, total));
                    lastBytes = current;
                    lastTime = now;
                }

                await Task.Delay(100, ct);
            }

            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            _ = await stdoutTask;

            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(stderr)
                    ? $"curl encerrou com código {process.ExitCode}."
                    : stderr.Trim());

            var finalSize = FileLengthSafe(tempPath);
            if (finalSize <= 0)
                throw new IOException("O servidor não retornou dados para o arquivo.");

            progress.Report((1, 0, true, finalSize, total > 0 ? total : finalSize));
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    if (nativePaused) SendUnixSignal(process.Id, "CONT");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }
            throw;
        }
    }

    private async Task<long> TryGetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            using var response = await _downloadHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            return response.IsSuccessStatusCode
                ? response.Content.Headers.ContentLength ?? 0
                : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryFindCurl(out string path)
    {
        path = string.Empty;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "curl");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            catch
            {
                // ignora entrada inválida do PATH
            }
        }

        foreach (var candidate in new[] { "/usr/bin/curl", "/bin/curl" })
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    private static string CurlQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", string.Empty).Replace("\n", string.Empty) + "\"";

    private static long FileLengthSafe(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void SendUnixSignal(int pid, string signal)
    {
        try
        {
            using var signalProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { $"-{signal}", pid.ToString() }
            });
            signalProcess?.WaitForExit(1000);
        }
        catch
        {
            // Se o sinal não puder ser enviado, o download continua; cancelar
            // ainda funciona pelo encerramento do processo principal.
        }
    }

    private async Task<(bool SupportsRanges, long Length)> ProbeRangeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            using var response = await _downloadHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode != HttpStatusCode.PartialContent)
                return (false, response.Content.Headers.ContentLength ?? 0);

            var length = response.Content.Headers.ContentRange?.Length ?? 0;
            return (length > 0, length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Falha na sondagem não deve impedir o download normal.
            return (false, 0);
        }
    }

    private async Task DownloadSegmentedAsync(
        string url,
        string tempPath,
        long totalLength,
        IProgress<(double fraction, double speed, bool turbo, long downloaded, long total)> progress,
        AsyncPauseGate? pauseGate,
        CancellationToken ct)
    {
        await using (var preallocate = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.ReadWrite,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            preallocate.SetLength(totalLength);
        }

        long downloaded = 0;
        var sw = Stopwatch.StartNew();
        var reportLock = new object();
        long lastBytes = 0;
        var lastTime = TimeSpan.Zero;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workerCt = linkedCts.Token;

        void ReportIfNeeded(bool force = false)
        {
            lock (reportLock)
            {
                var elapsed = sw.Elapsed;
                if (!force && elapsed - lastTime < TimeSpan.FromMilliseconds(500)) return;

                var current = Interlocked.Read(ref downloaded);
                var deltaBytes = current - lastBytes;
                var deltaSeconds = (elapsed - lastTime).TotalSeconds;
                var speed = deltaSeconds > 0 ? deltaBytes / deltaSeconds : 0;
                var fraction = totalLength > 0
                    ? Math.Clamp(current / (double)totalLength, 0, 1)
                    : 0;

                progress.Report((fraction, speed, true, current, totalLength));
                lastBytes = current;
                lastTime = elapsed;
            }
        }

        var segmentSize = (long)Math.Ceiling(totalLength / (double)ParallelSegments);
        var tasks = new List<Task>(ParallelSegments);

        for (var part = 0; part < ParallelSegments; part++)
        {
            var start = part * segmentSize;
            if (start >= totalLength) break;
            var end = Math.Min(totalLength - 1, start + segmentSize - 1);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                    using var response = await _downloadHttp.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        workerCt);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        linkedCts.Cancel();
                        throw new RangeNotSupportedException();
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(workerCt);
                    await using var file = new FileStream(
                        tempPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);

                    file.Seek(start, SeekOrigin.Begin);
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        long remaining = end - start + 1;
                        while (remaining > 0)
                        {
                            if (pauseGate is not null) await pauseGate.WaitAsync(workerCt);
                            var want = (int)Math.Min(buffer.Length, remaining);
                            var read = await input.ReadAsync(buffer.AsMemory(0, want), workerCt);
                            if (read <= 0) break;

                            await file.WriteAsync(buffer.AsMemory(0, read), workerCt);
                            remaining -= read;
                            Interlocked.Add(ref downloaded, read);
                            ReportIfNeeded();
                        }

                        if (remaining != 0)
                            throw new IOException("O servidor encerrou uma parte do download antes do esperado.");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch
                {
                    linkedCts.Cancel();
                    throw;
                }
            }, workerCt));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RangeNotSupportedException();
        }

        ReportIfNeeded(force: true);
        progress.Report((1, 0, true, totalLength, totalLength));
    }

    private async Task DownloadSingleStreamAsync(
        string url,
        string tempPath,
        IProgress<(double fraction, double speed, bool turbo, long downloaded, long total)> progress,
        AsyncPauseGate? pauseGate,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _downloadHttp.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long readTotal = 0;
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastTime = TimeSpan.Zero;

        try
        {
            while (true)
            {
                if (pauseGate is not null) await pauseGate.WaitAsync(ct);
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                if (sw.Elapsed - lastTime >= TimeSpan.FromMilliseconds(500))
                {
                    var deltaBytes = readTotal - lastBytes;
                    var deltaSeconds = (sw.Elapsed - lastTime).TotalSeconds;
                    var speed = deltaSeconds > 0 ? deltaBytes / deltaSeconds : 0;
                    var fraction = total > 0 ? Math.Clamp(readTotal / (double)total, 0, 1) : 0;
                    progress.Report((fraction, speed, false, readTotal, total));
                    lastBytes = readTotal;
                    lastTime = sw.Elapsed;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress.Report((1, 0, false, readTotal, total > 0 ? total : readTotal));
    }

    private static void FinalizeDownload(string tempPath, string output)
    {
        if (File.Exists(output)) File.Delete(output);
        File.Move(tempPath, output);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // O próximo FileMode.Create/Open vai produzir uma mensagem útil.
        }
    }

    private sealed class RangeNotSupportedException : Exception
    {
    }
}
