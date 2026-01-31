using IvyInsights.Models;
using IvyInsights.Services;
using Ivy.Helpers;

namespace IvyInsights.Apps;

internal class VersionChartDataItem
{
    public string Version { get; set; } = string.Empty;
    public double Downloads { get; set; }
}

[App(icon: Icons.ChartBar, title: "Ivy Statistics")]
public class IvyInsightsApp : ViewBase
{
    private const string PackageId = "Ivy";

    private static bool IsPreRelease(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        
        var parts = version.Split('-');
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]);
    }

    public override object? Build()
    {
        var client = UseService<IClientProvider>();
        var nugetProvider = UseService<INuGetStatisticsProvider>();
        
        var statsQuery = this.UseQuery(
            key: $"nuget-stats/{PackageId}",
            fetcher: async (CancellationToken ct) =>
            {
                var statistics = await nugetProvider.GetPackageStatisticsAsync(PackageId, ct);
                
                try
                {
                    client.Toast($"Successfully loaded statistics for {PackageId}!");
                }
                catch { }
                
                return statistics;
            },
            options: new QueryOptions
            {
                Scope = QueryScope.Server,
                Expiration = TimeSpan.FromMinutes(15),
                KeepPrevious = true,
                RevalidateOnMount = true
            },
            tags: ["nuget", "statistics"]);
        
        var animatedDownloads = this.UseState(0L);
        var animatedVersions = this.UseState(0);
        var refresh = this.UseRefreshToken();
        var hasAnimated = this.UseState(false);

        var versionChartDateRange = this.UseState<(DateOnly?, DateOnly?)>(() => (
            DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), 
            DateOnly.FromDateTime(DateTime.Today)));
        var versionChartShowPreReleases = this.UseState(true);
        var versionChartCount = this.UseState(7);

        var dbService = UseService<IDatabaseService>();
        var dailyStatsQuery = this.UseQuery(
            key: "daily-download-stats",
            fetcher: async (CancellationToken ct) =>
            {
                return await dbService.GetDailyDownloadStatsAsync(30, ct);
            },
            options: new QueryOptions
            {
                Scope = QueryScope.Server,
                Expiration = TimeSpan.FromMinutes(5),
                KeepPrevious = true,
                RevalidateOnMount = true
            },
            tags: ["database", "downloads"]);

        var starsStatsQuery = this.UseQuery(
            key: "github-stars-stats",
            fetcher: async (CancellationToken ct) =>
            {
                return await dbService.GetGithubStarsStatsAsync(30, ct);
            },
            options: new QueryOptions
            {
                Scope = QueryScope.Server,
                Expiration = TimeSpan.FromMinutes(5),
                KeepPrevious = true,
                RevalidateOnMount = true
            },
            tags: ["database", "stars"]);

        var filteredVersionChartQuery = this.UseQuery(
            key: $"version-chart-filtered/{PackageId}/{statsQuery.Value != null}/{versionChartDateRange.Value.Item1?.ToString("yyyy-MM-dd") ?? "null"}/{versionChartDateRange.Value.Item2?.ToString("yyyy-MM-dd") ?? "null"}/{versionChartShowPreReleases.Value}/{versionChartCount.Value}",
            fetcher: async (CancellationToken ct) =>
            {
                if (statsQuery.Value == null)
                    return new List<VersionChartDataItem>();

                var s = statsQuery.Value;
                var count = Math.Clamp(versionChartCount.Value, 2, 20);
                var filteredVersions = s.Versions.AsEnumerable();
                
                if (versionChartDateRange.Value.Item1.HasValue)
                {
                    var fromDate = versionChartDateRange.Value.Item1.Value.ToDateTime(TimeOnly.MinValue);
                    filteredVersions = filteredVersions.Where(v => 
                        v.Published.HasValue && v.Published.Value.Date >= fromDate);
                }
                if (versionChartDateRange.Value.Item2.HasValue)
                {
                    var toDate = versionChartDateRange.Value.Item2.Value.ToDateTime(TimeOnly.MinValue).AddDays(1);
                    filteredVersions = filteredVersions.Where(v => 
                        v.Published.HasValue && v.Published.Value.Date < toDate);
                }
                
                if (!versionChartShowPreReleases.Value)
                {
                    filteredVersions = filteredVersions.Where(v => !IsPreRelease(v.Version));
                }
                
                filteredVersions = filteredVersions.Where(v => v.Downloads.HasValue && v.Downloads.Value > 0);
                
                var versionChartData = filteredVersions
                    .OrderByDescending(v => v.Downloads)
                    .Take(count)
                    .Select(v => new VersionChartDataItem
                    { 
                        Version = v.Version, 
                        Downloads = (double)v.Downloads!.Value
                    })
                    .ToList();

                return versionChartData;
            },
            options: new QueryOptions
            {
                Scope = QueryScope.Server,
                Expiration = TimeSpan.Zero,
                KeepPrevious = false,
                RevalidateOnMount = false
            });

        if (statsQuery.Value != null && !hasAnimated.Value)
        {
            var animStats = statsQuery.Value;
            var animTotalDownloads = animStats.TotalDownloads ?? 0;
            var animTotalVersions = animStats.TotalVersions;

            var scheduler = new JobScheduler(maxParallelJobs: 3);
            var steps = 60;
            var delayMs = 15;

            scheduler.CreateJob("Animate Metrics")
                .WithAction(async (_, _, progress, token) =>
                {
                    for (int i = 0; i <= steps; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        var currentProgress = i / (double)steps;
                        animatedDownloads.Set((long)(animTotalDownloads * currentProgress));
                        animatedVersions.Set((int)(animTotalVersions * currentProgress));
                        refresh.Refresh();
                        progress.Report(currentProgress);
                        await Task.Delay(delayMs, token);
                    }
                    animatedDownloads.Set(animTotalDownloads);
                    animatedVersions.Set(animTotalVersions);
                    refresh.Refresh();
                })
                .Build();

            _ = Task.Run(async () => await scheduler.RunAsync());
            hasAnimated.Set(true);
        }

        if (statsQuery.Error is { } error)
        {
            return Layout.Center()
                | new Card(
                    Layout.Vertical().Gap(2).Padding(3)
                        | Text.H3("Error")
                        | Text.Block(error.Message)
                        | new Button("Retry", onClick: _ => statsQuery.Mutator.Revalidate())
                            .Icon(Icons.RefreshCcw)
                ).Width(Size.Fraction(0.5f));
        }

        if (statsQuery.Loading && statsQuery.Value == null)
        {
            return Layout.Vertical().Gap(4).Padding(4).Align(Align.TopCenter)
                | Text.H1("NuGet Statistics")
                | Text.Muted($"Loading statistics for {PackageId}...")
                | (Layout.Grid().Columns(4).Gap(3).Width(Size.Fraction(0.9f))
                    | new Skeleton().Height(Size.Units(80))
                    | new Skeleton().Height(Size.Units(80))
                    | new Skeleton().Height(Size.Units(80))
                    | new Skeleton().Height(Size.Units(80)))
                | (Layout.Grid().Columns(3).Gap(3).Width(Size.Fraction(0.9f))
                    | new Skeleton().Height(Size.Units(200))
                    | new Skeleton().Height(Size.Units(200))
                    | new Skeleton().Height(Size.Units(200)))
                | (Layout.Horizontal().Gap(3).Width(Size.Fraction(0.9f))
                    | new Skeleton().Width(Size.Fraction(0.6f)).Height(Size.Units(200))
                    | (Layout.Vertical().Width(Size.Full())
                        | new Skeleton().Height(Size.Units(200))
                        | new Skeleton().Height(Size.Units(200))));
        }

        var s = statsQuery.Value!;

        var mostDownloadedVersion = s.Versions
            .Where(v => v.Downloads.HasValue && v.Downloads.Value > 0)
            .OrderByDescending(v => v.Downloads)
            .FirstOrDefault();
        
        if (mostDownloadedVersion == null)
        {
            mostDownloadedVersion = s.Versions
                .OrderByDescending(v => v.Published ?? DateTime.MinValue)
                .FirstOrDefault();
        }


        var now = DateTime.UtcNow;
        var thisMonthStart = new DateTime(now.Year, now.Month, 1);
        var lastMonthStart = thisMonthStart.AddMonths(-1);
        var lastMonthEnd = thisMonthStart;
        
        var versionsLastMonth = s.Versions
            .Count(v => v.Published.HasValue && 
                       v.Published.Value >= lastMonthStart && 
                       v.Published.Value < lastMonthEnd);
        var versionsThisMonth = s.Versions
            .Count(v => v.Published.HasValue && 
                       v.Published.Value >= thisMonthStart && 
                       v.Published.Value < now);
        
        var downloadsLastMonth = s.Versions
            .Where(v => v.Published.HasValue && 
                       v.Published.Value >= lastMonthStart && 
                       v.Published.Value < lastMonthEnd &&
                       v.Downloads.HasValue)
            .Sum(v => v.Downloads!.Value);
        
        // Use daily stats from database
        var dailyStats = dailyStatsQuery.Value ?? new List<DailyDownloadStats>();
        
        // Get daily downloads for the last month (last 30 days)
        var last30DaysStart = now.AddDays(-30).Date;
        var dailyChartData = dailyStats
            .Where(d => d.Date >= DateOnly.FromDateTime(last30DaysStart))
            .OrderBy(d => d.Date)
            .Select(d => new
            {
                Date = d.Date.ToString("MMM dd"),
                Downloads = (double)Math.Max(0, d.DailyGrowth)
            })
            .ToList();

        var averageDailyDownloads = dailyChartData.Count > 0
            ? Math.Round(dailyChartData.Average(d => d.Downloads))
            : 0.0;

        var dailyChartDataWithAverage = dailyChartData
            .Select(d => new
            {
                d.Date,
                d.Downloads,
                Average = averageDailyDownloads
            })
            .ToList();

        // Calculate growth using last 14 days (This Week vs Previous Week) based on Dates
        var today = DateOnly.FromDateTime(now);
        var startOfThisWeek = today.AddDays(-6);
        var startOfPrevWeek = startOfThisWeek.AddDays(-7);
        
        var thisWeekDownloads = dailyStats
            .Where(d => d.Date >= startOfThisWeek && d.Date <= today)
            .Sum(d => Math.Max(0, d.DailyGrowth));
            
        var prevWeekDownloads = dailyStats
            .Where(d => d.Date >= startOfPrevWeek && d.Date < startOfThisWeek)
            .Sum(d => Math.Max(0, d.DailyGrowth));
        
        var growthPercent = 0.0;
        if (prevWeekDownloads > 0)
        {
            growthPercent = ((double)(thisWeekDownloads - prevWeekDownloads) / prevWeekDownloads) * 100;
        }
        else if (thisWeekDownloads > 0)
        {
            // If previous week is 0, show the current count as percentage growth (e.g. 0 -> 404 = +404%)
            growthPercent = (double)thisWeekDownloads;
        }
        
        var downloadsThisMonth = dailyStats
            .Where(d => d.Date.Year == now.Year && d.Date.Month == now.Month)
            .Sum(d => Math.Max(0, d.DailyGrowth));

        var avgMonthlyDownloads = dailyChartData.Count > 0 
            ? dailyChartData.Average(d => d.Downloads) * 30 // Average daily * 30 days
            : 0.0;

        var latestVersionInfo = s.Versions.FirstOrDefault(v => v.Version == s.LatestVersion);
        
        var trendIcon = growthPercent >= 0 ? Icons.TrendingUp : Icons.TrendingDown;
        var trendColor = growthPercent >= 0 ? Colors.Success : Colors.Destructive;

        var metrics = Layout.Grid().Columns(4).Gap(3)
            | new Card(
                Layout.Vertical().Gap(2).Padding(3).Align(Align.Center)
                    | (Layout.Horizontal().Gap(6).Align(Align.Center)
                        | Text.H2(animatedDownloads.Value.ToString("N0")).Bold()
                        | (thisWeekDownloads > 0 || prevWeekDownloads > 0
                            ? (Layout.Horizontal().Gap(1).Width(Size.Fit())
                                | new Icon(trendIcon).Color(trendColor)
                                | Text.H3($"{Math.Abs(growthPercent):0.0}%").Color(trendColor))
                            : null))
                    | Text.Block($"+{thisWeekDownloads:N0} this week").Muted()
            ).Title("Total Downloads").Icon(Icons.Download)
            | new Card(
                Layout.Vertical().Gap(2).Padding(3).Align(Align.Center)
                    | Text.H2(animatedVersions.Value.ToString("N0")).Bold()
                    | (versionsThisMonth > 0
                        ? Text.Block($"+{versionsThisMonth} this month").Muted()
                        : null)
            ).Title("Total Versions").Icon(Icons.Tag)
            | new Card(
                Layout.Vertical().Gap(2).Padding(3).Align(Align.Center)
                    | Text.H2(s.LatestVersion).Bold()
                    | (latestVersionInfo != null && latestVersionInfo.Downloads.HasValue && latestVersionInfo.Downloads.Value > 0
                        ? Text.Block($"{latestVersionInfo.Downloads.Value:N0} downloads").Muted()
                        : null)
            ).Title("Latest Version").Icon(Icons.ArrowUp)
            | new Card(
                Layout.Vertical().Gap(2).Padding(3).Align(Align.Center)
                    | Text.H2(mostDownloadedVersion != null 
                        ? mostDownloadedVersion.Version 
                        : "N/A").Bold()
                    | (mostDownloadedVersion != null && mostDownloadedVersion.Downloads.HasValue && mostDownloadedVersion.Downloads.Value > 0
                        ? Text.Block($"{mostDownloadedVersion.Downloads.Value:N0} downloads").Muted()
                        : null)
            ).Title("Most Popular").Icon(Icons.Star);

        var topVersionsData = s.Versions
            .Where(v => v.Published.HasValue && v.Published.Value >= now.AddDays(-30))
            .Where(v => v.Downloads.HasValue && v.Downloads.Value > 0)
            .OrderByDescending(v => v.Downloads)
            .Take(5)
            .Select(v => new
            {
                Version = v.Version,
                Downloads = (double)v.Downloads!.Value
            })
            .ToList();

        var topVersionsChart = topVersionsData.Count > 0
            ? topVersionsData.ToBarChart()
                .Dimension("Version", e => e.Version)
                .Measure("Downloads", e => e.Sum(f => f.Downloads))
            : null;

        var adoptionCard = topVersionsChart != null
            ? new Card(
                Layout.Vertical().Gap(3).Padding(3)
                    | topVersionsChart
            ).Title("Top Popular Versions (Last 30 Days)").Icon(Icons.Crown).Height(Size.Full())
            : new Card(
                Layout.Vertical().Gap(3).Padding(3).Align(Align.Center)
                    | Text.Block("No versions released in the last 30 days").Muted()
            ).Title("Top Popular Versions (Last 30 Days)").Icon(Icons.Crown).Height(Size.Full());

        var percentDiff = avgMonthlyDownloads > 0
            ? Math.Round(((downloadsThisMonth - avgMonthlyDownloads) / avgMonthlyDownloads) * 100, 1)
            : 0.0;

        var isGrowing = downloadsThisMonth > avgMonthlyDownloads;

        var dailyDownloadsChart = dailyChartDataWithAverage.Count > 0
            ? dailyChartDataWithAverage
                .ToLineChart(
                    dimension: d => d.Date,
                    measures: [
                        d => d.First().Downloads,
                        d => d.First().Average
                    ],
                    LineChartStyles.Dashboard)
            : null;

        var monthlyDownloadsCard = new Card(
            Layout.Vertical().Gap(3).Padding(3)
                | (dailyDownloadsChart != null
                    ? dailyDownloadsChart
                    : Text.Block("No data available for the last month").Muted())
        ).Title("Daily Downloads (Last 30 Days)")
         .Icon(Icons.ChartNoAxesCombined)
         .Height(Size.Full());


        var versionChartData = filteredVersionChartQuery.Value ?? new List<VersionChartDataItem>();

        var versionChart = versionChartData.Count > 0
            ? versionChartData.ToBarChart()
                .Dimension("Version", e => e.Version)
                .Measure("Downloads", e => e.Sum(f => f.Downloads))
            : null;

        var versionChartCard = new Card(
            Layout.Vertical().Gap(3).Padding(3)
                | (Layout.Horizontal().Gap(2).Align(Align.Center)
                    | versionChartDateRange.ToDateRangeInput()
                        .Format("MMM dd, yyyy")
                        .Placeholder("Select date range")
                    | new Button(versionChartShowPreReleases.Value ? "With Pre-releases" : "Releases Only")
                        .Outline()
                        .Icon(Icons.ChevronDown)
                        .WithDropDown(
                            MenuItem.Default("With Pre-releases").HandleSelect(() => versionChartShowPreReleases.Set(true)),
                            MenuItem.Default("Releases Only").HandleSelect(() => versionChartShowPreReleases.Set(false))
                        )
                    | new NumberInput<int>(versionChartCount)
                        .Min(2)
                        .Max(20)
                        .Width(Size.Units(60)))
                | (versionChart != null
                    ? versionChart
                    : Text.Block("No versions found").Muted())
        ).Title("Recent Versions Distribution").Icon(Icons.ChartBar);

        var starsStats = starsStatsQuery.Value ?? new List<GithubStarsStats>();
        
        var starsChartData = starsStats
            .OrderBy(d => d.Date)
            .Select(d => new 
            { 
                Date = d.Date.ToString("MMM dd"), 
                Stars = (double)d.Stars 
            })
            .ToList();

        var starsChart = starsChartData.Count > 0
            ? starsChartData.ToLineChart(
                dimension: e => e.Date,
                measures: [e => e.First().Stars],
                LineChartStyles.Dashboard)
            : null;

        var githubStarsCard = new Card(
            Layout.Vertical().Gap(3).Padding(3)
                | (starsChart != null 
                    ? starsChart 
                    : (object)Text.Block("No data available").Muted())
        ).Title("GitHub Stars (Last 30 Days)").Icon(Icons.Github);

        // Calculate historical weekly growth for the chart
        var growthWeeks = new List<string>();
        var growthValues = new List<double>();
        var todayDate = DateOnly.FromDateTime(now);
        
        // Find the Monday of the current week to align strictly to Mon-Sun
        // If today is Sunday (0), we go back 6 days to Monday. Otherwise we go back DayOfWeek-1
        var diff = todayDate.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)todayDate.DayOfWeek - 1;
        var currentWeekMonday = todayDate.AddDays(-diff);

        // Go back 12 calendar weeks
        for (int i = 0; i < 12; i++) 
        {
            var weekStart = currentWeekMonday.AddDays(-i * 7);
            var weekEnd = weekStart.AddDays(6); // Monday to Sunday
            var prevWeekStart = weekStart.AddDays(-7);
            
            var currentWeekSum = dailyStats
                .Where(d => d.Date >= weekStart && d.Date <= weekEnd)
                .Sum(d => Math.Max(0, d.DailyGrowth));
            
            var prevWeekSum = dailyStats
                .Where(d => d.Date >= prevWeekStart && d.Date < weekStart)
                .Sum(d => Math.Max(0, d.DailyGrowth));
                
            var growth = 0.0;
            if (prevWeekSum > 0)
            {
                growth = ((double)(currentWeekSum - prevWeekSum) / prevWeekSum) * 100;
            }
            else if (currentWeekSum > 0)
            {
               // If previous week is 0, show the current count as percentage growth to match the main KPI card
               growth = (double)currentWeekSum;
            }
            
            growthWeeks.Add(weekStart.ToString("MM/dd"));
            growthValues.Add(growth);
        }
        
        // Reverse to show oldest to newest
        growthWeeks.Reverse();
        growthValues.Reverse();

        var weeklyGrowthData = growthWeeks.Zip(growthValues, (w, g) => new { Week = w, Growth = g })
            .ToList();

        var weeklyGrowthChart = weeklyGrowthData.Count > 0
            ? weeklyGrowthData.ToLineChart(
                dimension: d => d.Week,
                measures: [d => d.First().Growth],
                LineChartStyles.Dashboard)
            : null;

        var weeklyGrowthCard = new Card(
            Layout.Vertical().Gap(3).Padding(3)
                | (weeklyGrowthChart != null 
                    ? weeklyGrowthChart 
                    : (object)Text.Block("No history available").Muted())
            ).Title("Weekly Growth (WoW %)").Icon(Icons.Activity);

        var allVersionsTable = s.Versions
            .Select(v => new
            {
                Version = v.Version,
                Published = v.Published.HasValue ? v.Published.Value.ToString("MMM dd, yyyy") : "N/A",
                Downloads = v.Downloads.HasValue ? v.Downloads.Value.ToString("N0") : "N/A"
            })
            .ToList();

        var versionsTable = allVersionsTable.AsQueryable()
            .ToDataTable()
            .Height(Size.Units(145))
            .Header(v => v.Version, "Version")
            .Header(v => v.Published, "Published")
            .Header(v => v.Downloads, "Downloads")
            .Config(c =>
            {
                c.AllowSorting = true;
                c.AllowFiltering = true;
                c.ShowSearch = true;
            });

        var versionsTableCard = new Card(
            Layout.Vertical().Gap(3).Padding(3)
                | versionsTable
        ).Title($"All Versions ({allVersionsTable.Count})").Icon(Icons.List).Width(Size.Fraction(0.6f));

        return Layout.Vertical().Gap(4).Padding(4).Align(Align.TopCenter)
            | metrics.Width(Size.Fraction(0.9f))
            | (Layout.Grid().Columns(3).Gap(3).Width(Size.Fraction(0.9f))
                | adoptionCard
                | monthlyDownloadsCard
                | weeklyGrowthCard)
            | (Layout.Horizontal().Gap(3).Width(Size.Fraction(0.9f))
                | versionsTableCard
                | (Layout.Vertical().Width(Size.Full())
                    | versionChartCard
                    | githubStarsCard ));
    }
}
