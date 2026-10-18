using IPTVDownloader.Models;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:5078");
builder.Services.AddSingleton<WebEngine>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = "2.0.36-web-beta", engine = "C# original" }));
app.MapGet("/api/state", async (WebEngine e) => Results.Ok(await e.GetStateAsync()));
app.MapPost("/api/connect", async (ConnectRequest req, WebEngine e) =>
{
    if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Informe o servidor, usuário e senha." });
    try
    {
        var result = await e.ConnectAsync(new XtreamAccount { Server = req.Server, Username = req.Username, Password = req.Password }, req.Remember);
        return result ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Usuário, senha ou servidor inválidos." });
    }
    catch { return Results.BadRequest(new { error = "Não foi possível conectar. Verifique o endereço, a rede e os dados da conta." }); }
});
app.MapPost("/api/connect-saved", async (SavedRequest req, WebEngine e) =>
{
    try { return await e.ConnectSavedAsync(req.Key) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Conta salva não encontrada ou conexão recusada." }); }
    catch { return Results.BadRequest(new { error = "Falha ao conectar a conta salva." }); }
});
app.MapGet("/api/catalog/{kind}", async (string kind, WebEngine e) =>
{
    if (kind is not ("movies" or "series")) return Results.BadRequest(new { error = "Tipo inválido." });
    try { return Results.Ok((await e.GetCatalogAsync(kind)).Select(WebEngine.Card)); }
    catch { return Results.BadRequest(new { error = "Não foi possível carregar a lista IPTV. Verifique a conexão." }); }
});
app.MapGet("/api/series/{id}/seasons", async (string id, WebEngine e) =>
{
    try { return Results.Ok((await e.GetSeasonsAsync(id)).Select(WebEngine.Card)); }
    catch { return Results.BadRequest(new { error = "Não foi possível carregar as temporadas." }); }
});
app.MapGet("/api/series/{id}/season/{season:int}", async (string id, int season, WebEngine e) =>
{
    try { return Results.Ok((await e.GetEpisodesAsync(id, season)).Select(WebEngine.Card)); }
    catch { return Results.BadRequest(new { error = "Não foi possível carregar os episódios." }); }
});
app.MapPost("/api/queue", async (QueueRequest req, WebEngine e) =>
{
    try { return Results.Ok(new { added = await e.EnqueueAsync(req.Keys ?? []) }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch { return Results.BadRequest(new { error = "Não foi possível adicionar os itens." }); }
});
app.MapGet("/api/downloads", (WebEngine e) => Results.Ok(e.DownloadState()));
app.MapPost("/api/downloads/pause", (WebEngine e) => { e.Pause(); return Results.Ok(e.DownloadState()); });
app.MapPost("/api/downloads/resume", (WebEngine e) => { e.Resume(); return Results.Ok(e.DownloadState()); });
app.MapPost("/api/downloads/cancel", (WebEngine e) => { e.Cancel(); return Results.Ok(e.DownloadState()); });
app.MapPost("/api/downloads/{id}/retry", (string id, RetryRequest req, WebEngine e) =>
{
    try { e.Retry(id, req.Restart); return Results.Ok(e.DownloadState()); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch { return Results.BadRequest(new { error = "Não foi possível preparar a retomada. Verifique o armazenamento." }); }
});
app.MapPost("/api/downloads/{id}/control", (string id, JobControlRequest req, WebEngine e) =>
{
    try { e.ControlJob(id, req.Action); return Results.Ok(e.DownloadState()); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch { return Results.BadRequest(new { error = "Não foi possível alterar o download. Verifique o armazenamento." }); }
});
app.MapPost("/api/downloads/{id}/open-folder", (string id, WebEngine e) =>
{
    try { return Results.Ok(e.OpenDownloadFolder(id)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch { return Results.BadRequest(new { error = "Não foi possível abrir a pasta do arquivo." }); }
});
app.MapPost("/api/downloads/{id}/delete", (string id, WebEngine e) =>
{
    try { e.DeleteDownload(id); return Results.Ok(e.DownloadState()); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch { return Results.BadRequest(new { error = "Não foi possível excluir o arquivo." }); }
});
app.MapPost("/api/settings", async (SettingsRequest req, WebEngine e) =>
{
    try { await e.SaveSettingsAsync(req); return Results.Ok(new { ok = true }); }
    catch { return Results.BadRequest(new { error = "Não foi possível salvar as configurações." }); }
});
app.MapFallbackToFile("index.html");
await app.Services.GetRequiredService<WebEngine>().StartAsync();
app.Run();

public sealed record ConnectRequest(string Server, string Username, string Password, bool Remember = false);
public sealed record SavedRequest(string Key);
public sealed record QueueRequest(List<string>? Keys);
public sealed record SettingsRequest(string? DownloadParent, bool? HideAdult, int? DownloadConnections = null);

public sealed record RetryRequest(bool Restart = false);

public sealed record JobControlRequest(string Action);
