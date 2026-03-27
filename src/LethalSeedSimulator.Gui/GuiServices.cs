using System.Text;
using System.Text.Json;
using LethalSeedSimulator.Core;
using LethalSeedSimulator.Extractor;
using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Gui;

internal sealed class GuiServices
{
    private readonly SeedSimulator simulator = new();
    private readonly RuleExtractorRegistry extractorRegistry = new([new CurrentDumpExtractorAdapter()]);
    private readonly JsonVersionRuleProvider ruleProvider;

    public GuiServices(string rulesRoot)
    {
        RulesRoot = rulesRoot;
        ruleProvider = new JsonVersionRuleProvider(rulesRoot);
    }

    public string RulesRoot { get; }

    public IReadOnlyList<string> InstalledRulePacks() => ruleProvider.ListVersions();

    public IReadOnlyList<string> ExtractorAdapters() => extractorRegistry.ListVersions();

    public IReadOnlyList<LevelRule> GetLevels(string version)
    {
        var pack = ruleProvider.Load(version);
        return pack.Levels.OrderBy(x => int.TryParse(x.Id, out var id) ? id : int.MaxValue).ToList();
    }

    public void ExtractRulePack(string version, string sourceRoot)
    {
        var adapter = extractorRegistry.Resolve(version);
        var pack = adapter.Extract(sourceRoot);
        var outputDir = Path.Combine(RulesRoot, version);
        Directory.CreateDirectory(outputDir);
        var outputFile = Path.Combine(outputDir, "rulepack.json");
        File.WriteAllText(outputFile, JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
    }

    public SeedReport Inspect(string version, string levelId, int seed)
    {
        var pack = ruleProvider.Load(version);
        return simulator.Simulate(pack, levelId, seed);
    }

    public IReadOnlyList<SeedReport> Search(string version, string levelId, int seedStart, int seedEnd, string query, int maxDegreeOfParallelism)
    {
        var pack = ruleProvider.Load(version);
        var predicate = SeedQuery.Compile(query);
        var gate = new object();
        var hits = new List<SeedReport>();

        Parallel.ForEach(Partition(seedStart, seedEnd), new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, range =>
        {
            var local = new List<SeedReport>();
            for (var seed = range.start; seed <= range.end; seed++)
            {
                var report = simulator.Simulate(pack, levelId, seed);
                if (predicate(report))
                {
                    local.Add(report);
                }
            }

            if (local.Count == 0)
            {
                return;
            }

            lock (gate)
            {
                hits.AddRange(local);
            }
        });

        return hits.OrderBy(x => x.Seed).ToList();
    }

    public void ExportCsv(
        string version,
        string levelId,
        int seedStart,
        int seedEnd,
        string outputCsv,
        int reportInterval,
        bool includeRollsJson,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var pack = ruleProvider.Load(version);
        var level = pack.Levels.FirstOrDefault(x => string.Equals(x.Id, levelId, StringComparison.OrdinalIgnoreCase))
            ?? pack.Levels.FirstOrDefault(x => string.Equals(x.Name, levelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown level id '{levelId}'.");

        var allItemIds = level.SpawnableScrap.Select(x => x.ItemId).Distinct().OrderBy(x => x).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputCsv) ?? ".");

        using var stream = new FileStream(outputCsv, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        var headerColumns = new List<string> { "seed", "weather", "scrap_count", "total_scrap_value", "goldbar_only" };
        headerColumns.AddRange(allItemIds.Select(id => $"item_{id}_count"));
        if (includeRollsJson)
        {
            headerColumns.Add("rolls_json");
        }
        writer.WriteLine(string.Join(",", headerColumns));

        var started = DateTime.UtcNow;
        for (var seed = seedStart; seed <= seedEnd; seed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = simulator.Simulate(pack, levelId, seed);
            var row = new List<string>
            {
                seed.ToString(),
                CsvEscape(report.Weather),
                report.ScrapCount.ToString(),
                report.TotalScrapValue.ToString(),
                report.ScrapRolls.All(x => x.ItemId == 152767) ? "1" : "0"
            };

            foreach (var itemId in allItemIds)
            {
                row.Add(report.ItemCounts.TryGetValue(itemId, out var count) ? count.ToString() : "0");
            }

            if (includeRollsJson)
            {
                row.Add(CsvEscape(JsonSerializer.Serialize(report.ScrapRolls)));
            }

            writer.WriteLine(string.Join(",", row));
            var done = seed - seedStart + 1;
            if (done % reportInterval == 0)
            {
                writer.Flush();
                var elapsed = DateTime.UtcNow - started;
                var perSecond = done / Math.Max(elapsed.TotalSeconds, 0.001);
                var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
                progress?.Report($"Processed {done:N0} rows @ {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
            }
        }

        writer.Flush();
        progress?.Report($"Export complete: {outputCsv}");
    }

    private static IEnumerable<(int start, int end)> Partition(int start, int end)
    {
        const int chunkSize = 25000;
        for (var cursor = start; cursor <= end; cursor += chunkSize)
        {
            var chunkEnd = Math.Min(cursor + chunkSize - 1, end);
            yield return (cursor, chunkEnd);
        }
    }

    private static string CsvEscape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
