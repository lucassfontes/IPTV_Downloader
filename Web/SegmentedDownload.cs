using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using IPTVDownloader.Infrastructure;
using IPTVDownloader.Models;

namespace IPTVDownloader.Services;

public sealed partial class DownloadService
{
    public static (long Bytes, long Total) SegmentedProgress(string output)
    {
        var dir = output + ".segments";
        var plan = ReadJson<SegmentPlan>(Path.Combine(dir, "plan.json"));
        if (plan == null) return (0, 0);
        return (Directory.EnumerateFiles(dir, "*.part").Sum(p => new FileInfo(p).Length), plan.Total);
    }
    private sealed record SegmentPlan(string? ETag, DateTimeOffset? Modified, long Total, int Count);
    private async Task<bool> TrySegmentedAsync(string url, string output, int connections,
        IProgress<DownloadProgress> progress, AsyncPauseGate? pause, CancellationToken ct)
    {
        var directory = output + ".segments";
        var planPath = Path.Combine(directory, "plan.json");
        var previous = ReadJson<SegmentPlan>(planPath);
        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        SegmentPlan plan;
        using (var response = await _downloadHttp.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token))
        {
            var range = response.Content.Headers.ContentRange;
            var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
            var modified = response.Content.Headers.LastModified;
            var total = range?.Length ?? 0;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
                || range.From != 0 || range.To != 0 || total <= 0 || (etag == null && modified == null))
            {
                if (Directory.Exists(directory))
                    throw new DownloadProblem("Servidor não permite retomar as partes com segurança. Use Reiniciar download.");
                progress.Report(new DownloadProgress("Servidor sem suporte às partes; usando uma conexão.", 0, 0, 0, 0, 1));
                return false;
            }
            if (previous != null)
            {
                if (previous.Total != total || previous.ETag != etag || (etag == null && previous.Modified != modified))
                    throw new DownloadProblem("Arquivo remoto mudou. Reinicie o download para criar novas partes.");
                if (previous.Count is not (1 or 2 or 4 or 8 or 16))
                    throw new DownloadProblem("Plano de partes inválido. Reinicie o download.");
                plan = previous;
            }
            else
            {
                if (Directory.Exists(directory))
                    throw new DownloadProblem("Partes sem plano de recuperação válido. Use Reiniciar download.");
                // Do not split small files into many tiny connections.
                var count = connections;
                while (count > 1 && total / count < 1024 * 1024) count /= 2;
                if (count == 1) return false;
                plan = new SegmentPlan(etag, modified, total, count);
                Directory.CreateDirectory(directory);
                AtomicJson(planPath, plan);
            }
        }
        var lengths = new long[plan.Count];
        var starts = new long[plan.Count];
        var sizes = new long[plan.Count];
        for (var i = 0; i < plan.Count; i++)
        {
            starts[i] = plan.Total / plan.Count * i + Math.Min(i, plan.Total % plan.Count);
            sizes[i] = plan.Total / plan.Count + (i < plan.Total % plan.Count ? 1 : 0);
            var path = Path.Combine(directory, $"{i:D2}.part");
            lengths[i] = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (lengths[i] == sizes[i] && !File.Exists(path + ".done")) lengths[i] = 0;
            if (lengths[i] > sizes[i]) throw new DownloadProblem("Parte maior que o esperado. Reinicie o download.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new object();
        var clock = Stopwatch.StartNew();
        long lastBytes = lengths.Sum();
        var lastTime = TimeSpan.Zero;
        void Report(bool force = false)
        {
            lock (gate)
            {
                var elapsed = clock.Elapsed;
                if (!force && elapsed - lastTime < TimeSpan.FromMilliseconds(750)) return;
                var current = lengths.Select((_, i) => Interlocked.Read(ref lengths[i])).Sum();
                var speed = (current - lastBytes) / Math.Max(.001, (elapsed - lastTime).TotalSeconds);
                progress.Report(new DownloadProgress($"Baixando em {plan.Count} partes", current / (double)plan.Total,
                    0, speed, 0, 1, current, plan.Total));
                lastBytes = current; lastTime = elapsed;
            }
        }
        Report(true);
        var tasks = Enumerable.Range(0, plan.Count).Select(async index =>
        {
            try
            {
                if (lengths[index] == sizes[index]) return;
                if (pause != null) await pause.WaitAsync(linked.Token);
                var start = starts[index] + lengths[index];
                var end = starts[index] + sizes[index] - 1;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                request.Headers.IfRange = plan.ETag != null
                    ? new RangeConditionHeaderValue(new EntityTagHeaderValue(plan.ETag))
                    : new RangeConditionHeaderValue(plan.Modified!.Value);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
                    || range.From != start || range.To != end || range.Length != plan.Total
                    || (response.Content.Headers.ContentLength is long bytes && bytes != end - start + 1)
                    || (plan.ETag != null && response.Headers.ETag?.ToString() != plan.ETag)
                    || (plan.ETag == null && response.Content.Headers.LastModified != plan.Modified))
                    throw new DownloadProblem("Servidor recusou ou alterou uma parte. Tente novamente; se persistir, escolha 1 conexão e Reiniciar.");
                await using var input = await response.Content.ReadAsStreamAsync(linked.Token);
                await using var file = new FileStream(Path.Combine(directory, $"{index:D2}.part"), lengths[index] > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, 131072, FileOptions.Asynchronous);
                var buffer = new byte[131072];
                var flushClock = Stopwatch.StartNew();
                while (lengths[index] < sizes[index])
                {
                    if (pause != null) await pause.WaitAsync(linked.Token);
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var want = (int)Math.Min(buffer.Length, sizes[index] - lengths[index]);
                    var read = await input.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token);
                    if (read == 0) throw new DownloadProblem("Uma parte terminou antes do esperado. Use Tentar novamente.");
                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    Interlocked.Add(ref lengths[index], read);
                    if (flushClock.Elapsed >= TimeSpan.FromSeconds(1)) { file.Flush(true); flushClock.Restart(); }
                    Report();
                }
                file.Flush(true);
                // Validate end of response as well as expected byte count.
                using var endTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                endTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                if (await input.ReadAsync(buffer.AsMemory(0, 1), endTimeout.Token) != 0)
                    throw new DownloadProblem("Servidor enviou dados além da parte solicitada. Reinicie com uma conexão.");
                AtomicJson(Path.Combine(directory, $"{index:D2}.part.done"), true);
            }
            catch { linked.Cancel(); throw; }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch
        {
            ct.ThrowIfCancellationRequested();
            var problem = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.Flatten().InnerExceptions)
                .OfType<DownloadProblem>().FirstOrDefault();
            throw problem ?? new DownloadProblem("Download em partes interrompido. Tente novamente ou reduza as conexões e reinicie.");
        }
        ct.ThrowIfCancellationRequested();
        progress.Report(new DownloadProgress("Unindo e verificando partes", 1, 0, 0, 0, 1, plan.Total, plan.Total));
        var mergePath = output + ".merge";
        await using (var merged = new FileStream(mergePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        {
            for (var i = 0; i < plan.Count; i++)
            {
                if (pause != null) await pause.WaitAsync(ct);
                var path = Path.Combine(directory, $"{i:D2}.part");
                if (new FileInfo(path).Length != sizes[i]) throw new DownloadProblem("Parte incompleta antes da união. Tente novamente.");
                await using var input = File.OpenRead(path);
                await input.CopyToAsync(merged, ct);
            }
            merged.Flush(true);
        }
        if (new FileInfo(mergePath).Length != plan.Total) throw new DownloadProblem("Tamanho final inconsistente. Partes preservadas.");
        var hash = await HashAsync(mergePath, ct);
        var receiptPath = output + ".verified.json";
        AtomicJson(receiptPath, new Receipt(plan.Total, hash));
        File.Move(mergePath, output);
        // Limpeza somente após a união e verificação terem sido concluídas.
        if (File.Exists(receiptPath)) File.Delete(receiptPath);
        try { Directory.Delete(directory, true); } catch (IOException) { /* Verified output remains usable. */ }
        progress.Report(new DownloadProgress("Concluído", 1, 1, 0, 1, 1, plan.Total, plan.Total));
        return true;
    }
}
