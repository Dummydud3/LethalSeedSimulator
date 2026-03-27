using System.Text.Json;
using LethalSeedSimulator.Core;
using LethalSeedSimulator.Extractor;
using LethalSeedSimulator.Rules;

var registry = new RuleExtractorRegistry([new CurrentDumpExtractorAdapter()]);
var simulator = new SeedSimulator();

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

try
{
    var command = args[0].ToLowerInvariant();
    var options = ParseOptions(args.Skip(1).ToArray());
    var rulesRoot = ResolveRulesRoot(options);
    var sourceRoot = ResolveSourceRoot(options);
    var provider = new JsonVersionRuleProvider(rulesRoot);

    switch (command)
    {
        case "extract":
            RunExtract(options, sourceRoot, rulesRoot);
            break;
        case "inspect":
            RunInspect(options, provider);
            break;
        case "search":
            RunSearch(options, provider);
            break;
        case "validate":
            RunValidate(options, provider);
            break;
        case "export-all":
            RunExportAll(options, provider);
            break;
        case "versions":
            RunVersions(provider);
            break;
        default:
            throw new InvalidOperationException($"Unknown command '{command}'.");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return 0;

void RunExtract(Dictionary<string, string> options, string sourceRoot, string rulesRoot)
{
    var version = Get(options, "version", CurrentDumpExtractorAdapter.AdapterVersion);
    var adapter = registry.Resolve(version);
    var outputRoot = Get(options, "rules-root", rulesRoot);

    var pack = adapter.Extract(sourceRoot);
    var outputDir = Path.Combine(outputRoot, version);
    Directory.CreateDirectory(outputDir);

    var json = JsonSerializer.Serialize(pack, JsonOptions());
    File.WriteAllText(Path.Combine(outputDir, "rulepack.json"), json);
    Console.WriteLine($"Wrote rule pack: {Path.Combine(outputDir, "rulepack.json")}");
}

void RunInspect(Dictionary<string, string> options, JsonVersionRuleProvider provider)
{
    var version = Require(options, "version");
    var level = Get(options, "moon", "0");
    var seed = int.Parse(Require(options, "seed"));

    var pack = provider.Load(version);
    var report = simulator.Simulate(pack, level, seed);
    Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions()));
}

void RunSearch(Dictionary<string, string> options, JsonVersionRuleProvider provider)
{
    var version = Require(options, "version");
    var level = Get(options, "moon", "0");
    var seedStart = int.Parse(Require(options, "seed-start"));
    var seedEnd = int.Parse(Require(options, "seed-end"));
    var query = Get(options, "query", string.Empty);
    var threads = int.Parse(Get(options, "threads", Environment.ProcessorCount.ToString()));
    var jsonl = Get(options, "jsonl", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

    var pack = provider.Load(version);
    var predicate = SeedQuery.Compile(query);
    var hits = new List<SeedReport>();
    var gate = new object();

    Parallel.ForEach(
        Partitioner(seedStart, seedEnd),
        new ParallelOptions { MaxDegreeOfParallelism = threads },
        range =>
        {
            var local = new List<SeedReport>();
            for (var seed = range.start; seed <= range.end; seed++)
            {
                var report = simulator.Simulate(pack, level, seed);
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

    foreach (var hit in hits.OrderBy(x => x.Seed))
    {
        if (jsonl)
        {
            Console.WriteLine(JsonSerializer.Serialize(hit, JsonOptions()));
            continue;
        }

        Console.WriteLine($"{hit.Seed}: weather={hit.Weather}, scrap={hit.ScrapCount}, total={hit.TotalScrapValue}");
    }

    Console.WriteLine($"Matches: {hits.Count}");
}

void RunValidate(Dictionary<string, string> options, JsonVersionRuleProvider provider)
{
    var version = Require(options, "version");
    var level = Get(options, "moon", "0");
    var seed = int.Parse(Get(options, "seed", "123456"));
    var pack = provider.Load(version);

    var first = simulator.Simulate(pack, level, seed);
    var second = simulator.Simulate(pack, level, seed);
    var firstJson = JsonSerializer.Serialize(first, JsonOptions());
    var secondJson = JsonSerializer.Serialize(second, JsonOptions());

    if (!string.Equals(firstJson, secondJson, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Determinism check failed.");
    }

    Console.WriteLine("Validation passed.");
}

void RunVersions(JsonVersionRuleProvider provider)
{
    Console.WriteLine("Extractor adapters:");
    foreach (var version in registry.ListVersions())
    {
        Console.WriteLine($"  {version}");
    }

    Console.WriteLine("Installed rule packs:");
    foreach (var version in provider.ListVersions())
    {
        Console.WriteLine($"  {version}");
    }
}

void RunExportAll(Dictionary<string, string> options, JsonVersionRuleProvider provider)
{
    var version = Require(options, "version");
    var levelId = Get(options, "moon", "0");
    var seedStart = int.Parse(Get(options, "seed-start", "0"));
    var seedEnd = int.Parse(Get(options, "seed-end", "99999999"));
    var output = Get(
        options,
        "output",
        Path.Combine(ResolveExportRoot(options), $"{version}_{levelId}_{seedStart}_{seedEnd}.csv"));

    var reportInterval = int.Parse(Get(options, "report-interval", "1000000"));
    var includeRolls = Get(options, "include-rolls-json", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

    if (seedEnd < seedStart)
    {
        throw new InvalidOperationException("--seed-end must be >= --seed-start");
    }

    var pack = provider.Load(version);
    var level = pack.Levels.FirstOrDefault(x => string.Equals(x.Id, levelId, StringComparison.OrdinalIgnoreCase))
        ?? pack.Levels.FirstOrDefault(x => string.Equals(x.Name, levelId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown level id '{levelId}'.");

    var allItemIds = level.SpawnableScrap
        .Select(x => x.ItemId)
        .Distinct()
        .OrderBy(x => x)
        .ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");

    using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(stream);

    var headerColumns = new List<string>
    {
        "seed",
        "weather",
        "scrap_count",
        "total_scrap_value",
        "goldbar_only"
    };
    headerColumns.AddRange(allItemIds.Select(id => $"item_{id}_count"));
    if (includeRolls)
    {
        headerColumns.Add("rolls_json");
    }

    writer.WriteLine(string.Join(",", headerColumns));

    var started = DateTime.UtcNow;
    for (var seed = seedStart; seed <= seedEnd; seed++)
    {
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

        if (includeRolls)
        {
            row.Add(CsvEscape(JsonSerializer.Serialize(report.ScrapRolls)));
        }

        writer.WriteLine(string.Join(",", row));

        if ((seed - seedStart + 1) % reportInterval == 0)
        {
            writer.Flush();
            var done = seed - seedStart + 1;
            var elapsed = DateTime.UtcNow - started;
            var perSecond = done / Math.Max(elapsed.TotalSeconds, 0.001);
            var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
            Console.WriteLine(
                $"Processed {done:N0} rows, {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
        }
    }

    writer.Flush();
    Console.WriteLine($"Export complete: {output}");
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = token[2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : "true";
        map[key] = value;
    }

    return map;
}

static IEnumerable<(int start, int end)> Partitioner(int start, int end)
{
    const int chunk = 25000;
    for (var cursor = start; cursor <= end; cursor += chunk)
    {
        var chunkEnd = Math.Min(cursor + chunk - 1, end);
        yield return (cursor, chunkEnd);
    }
}

static string ResolveRulesRoot(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("rules-root", out var fromOption) && !string.IsNullOrWhiteSpace(fromOption))
    {
        return fromOption;
    }

    var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_RULES_ROOT");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        return fromEnv;
    }

    var cwdRules = Path.Combine(Directory.GetCurrentDirectory(), "rules");
    if (Directory.Exists(cwdRules))
    {
        return cwdRules;
    }

    var nestedRules = Path.Combine(Directory.GetCurrentDirectory(), "LethalSeedSimulator", "rules");
    if (Directory.Exists(nestedRules))
    {
        return nestedRules;
    }

    return cwdRules;
}

static string ResolveSourceRoot(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("source-root", out var fromOption) && !string.IsNullOrWhiteSpace(fromOption))
    {
        return fromOption;
    }

    var fromEnv = Environment.GetEnvironmentVariable("LETHAL_SIM_SOURCE_ROOT");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        return fromEnv;
    }

    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "Assembly-CSharp")) &&
            Directory.Exists(Path.Combine(current.FullName, "Assets", "MonoBehaviour")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException(
        "Could not resolve source root. Pass --source-root or set LETHAL_SIM_SOURCE_ROOT.");
}

static string ResolveExportRoot(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("export-root", out var fromOption) && !string.IsNullOrWhiteSpace(fromOption))
    {
        return fromOption;
    }

    var cwd = Directory.GetCurrentDirectory();
    var defaultDir = Path.Combine(cwd, "exports");
    return defaultDir;
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true
};

static string Require(IReadOnlyDictionary<string, string> options, string key)
{
    if (!options.TryGetValue(key, out var value))
    {
        throw new InvalidOperationException($"Missing required option --{key}");
    }

    return value;
}

static string Get(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
    options.TryGetValue(key, out var value) ? value : fallback;

static void PrintHelp()
{
    Console.WriteLine("Lethal Seed Simulator");
    Console.WriteLine("Commands:");
    Console.WriteLine("  extract --version <key> [--source-root <path>] [--rules-root <path>]");
    Console.WriteLine("  inspect --version <key> --seed <n> [--moon <idOrName>] [--rules-root <path>]");
    Console.WriteLine("  search --version <key> --seed-start <a> --seed-end <b> [--moon <idOrName>] [--query <expr>] [--threads <n>] [--jsonl true] [--rules-root <path>]");
    Console.WriteLine("  validate --version <key> [--moon <idOrName>] [--seed <n>] [--rules-root <path>]");
    Console.WriteLine("  export-all --version <key> [--moon <idOrName>] [--seed-start <n>] [--seed-end <n>] [--output <csv>] [--export-root <path>] [--report-interval <n>] [--include-rolls-json true] [--rules-root <path>]");
    Console.WriteLine("  versions");
}

static string CsvEscape(string value)
{
    if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
    {
        return value;
    }

    return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
