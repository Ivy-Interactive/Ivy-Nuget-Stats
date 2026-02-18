using IvyInsights.Apps;
using IvyInsights.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

var server = new Server();

server.Services.AddHttpClient<NuGetApiClient>(client =>
{
    // Timeout: NuGet API can be slow, especially registration pages (10-30 seconds recommended)
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("IvyInsights", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
    client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("deflate"));
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
});

server.Services.AddSingleton<INuGetStatisticsProvider, NuGetStatisticsProvider>();
server.Services.AddSingleton<IDatabaseService, DatabaseService>();

server.Services.AddHttpClient<IDatabaseUpdateService, DatabaseUpdateService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("IvyInsights/1.0");
});

#if DEBUG
server.UseHotReload();
#endif

server.AddAppsFromAssembly();
server.AddConnectionsFromAssembly();
server.ReservePaths("/starred", "/unstarred", "/stars");

server.UseWebApplication(app =>
{
    app.MapGet("/starred", async (IDatabaseService db, CancellationToken ct) =>
    {
        var all = await db.GetGithubStargazersAsync(ct);
        var starred = all.Where(s => s.IsActive).Select(s => new { s.Username, s.StarredAt });
        return Results.Ok(starred);
    });

    app.MapGet("/unstarred", async (IDatabaseService db, CancellationToken ct) =>
    {
        var all = await db.GetGithubStargazersAsync(ct);
        var unstarred = all.Where(s => !s.IsActive).Select(s => new { s.Username, s.StarredAt, s.UnstarredAt });
        return Results.Ok(unstarred);
    });

    app.MapGet("/stars/count", async (IDatabaseService db, CancellationToken ct) =>
    {
        var all = await db.GetGithubStargazersAsync(ct);
        var starred = all.Count(s => s.IsActive);
        var unstarred = all.Count(s => !s.IsActive);
        return Results.Ok(new { starred, unstarred, totalEver = all.Count });
    });
});

await server.RunAsync();