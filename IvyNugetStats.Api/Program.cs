using Npgsql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("ivy-nuget-stats", options =>
{
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "Ivy NuGet Stats API";
        doc.Info.Description = "Public API for GitHub stargazers data of the Ivy Framework repository.";
        doc.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

var connStr = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
    ?? builder.Configuration["DB_CONNECTION_STRING"]
    ?? throw new InvalidOperationException(
        "DB_CONNECTION_STRING is not set. Use: dotnet user-secrets set \"DB_CONNECTION_STRING\" \"<connection-string>\"");

var npgsqlBuilder = new NpgsqlConnectionStringBuilder(connStr);
var dataSource = NpgsqlDataSource.Create(npgsqlBuilder.ConnectionString);
builder.Services.AddSingleton(dataSource);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/scalar", options =>
    {
        options.Title = "Ivy NuGet Stats API";
        options.WithOpenApiRoutePattern("/openapi/ivy-nuget-stats.json");
    });
}

app.UseHttpsRedirection();

const string Repo = "Ivy-Interactive/Ivy-Framework";

app.MapGet("/starred", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    var results = new List<StargazerDto>();
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
        SELECT user_login, starred_at
        FROM github_stargazers
        WHERE repo_name = @repo AND unstarred_at IS NULL
        ORDER BY starred_at DESC
        """, conn);
    cmd.Parameters.AddWithValue("repo", Repo);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        results.Add(new StargazerDto(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1)));
    return Results.Ok(results);
})
.WithName("GetStarred")
.WithSummary("Get active stargazers")
.WithDescription("Returns all GitHub users who currently have the repository starred.");

app.MapGet("/unstarred", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    var results = new List<UnstarredDto>();
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
        SELECT user_login, starred_at, unstarred_at
        FROM github_stargazers
        WHERE repo_name = @repo AND unstarred_at IS NOT NULL
        ORDER BY unstarred_at DESC
        """, conn);
    cmd.Parameters.AddWithValue("repo", Repo);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        results.Add(new UnstarredDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
    return Results.Ok(results);
})
.WithName("GetUnstarred")
.WithSummary("Get removed stargazers")
.WithDescription("Returns all GitHub users who previously starred the repository but have since removed their star.");

app.MapGet("/stars/count", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand("""
        SELECT
            COUNT(*) FILTER (WHERE unstarred_at IS NULL)     AS starred,
            COUNT(*) FILTER (WHERE unstarred_at IS NOT NULL) AS unstarred,
            COUNT(*)                                          AS total_ever
        FROM github_stargazers
        WHERE repo_name = @repo
        """, conn);
    cmd.Parameters.AddWithValue("repo", Repo);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return Results.Ok(new StarsCountDto(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2)));
})
.WithName("GetStarsCount")
.WithSummary("Get star counts")
.WithDescription("Returns total number of active stargazers, users who unstarred, and all-time unique stargazers.");

app.Run();

record StargazerDto(string Username, DateTime? StarredAt);
record UnstarredDto(string Username, DateTime? StarredAt, DateTime? UnstarredAt);
record StarsCountDto(long Starred, long Unstarred, long TotalEver);
