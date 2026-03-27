using System.Text.Json;
using LethalSeedSimulator.Core;
using LethalSeedSimulator.Extractor;
using LethalSeedSimulator.Rules;
using Microsoft.Data.Sqlite;

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
    var runSeed = int.Parse(Get(options, "run-seed", Require(options, "seed")));
    var weatherSeed = int.Parse(Get(options, "weather-seed", Math.Max(runSeed - 1, 0).ToString()));
    var players = int.Parse(Get(options, "players", "0"));
    var streak = int.Parse(Get(options, "streak-days", "0"));

    var pack = provider.Load(version);
    var report = simulator.Simulate(pack, new SimulationRequest
    {
        LevelId = level,
        RunSeed = runSeed,
        WeatherSeed = weatherSeed,
        IsChallengeFile = false,
        ConnectedPlayersOnServer = players,
        DaysPlayersSurvivedInARow = streak
    });
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
                var report = simulator.Simulate(pack, new SimulationRequest
                {
                    LevelId = level,
                    RunSeed = seed,
                    WeatherSeed = Math.Max(seed - 1, 0),
                    IsChallengeFile = false,
                    ConnectedPlayersOnServer = 0,
                    DaysPlayersSurvivedInARow = 0
                });
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

    var first = simulator.Simulate(pack, new SimulationRequest
    {
        LevelId = level,
        RunSeed = seed,
        WeatherSeed = Math.Max(seed - 1, 0),
        IsChallengeFile = false,
        ConnectedPlayersOnServer = 0,
        DaysPlayersSurvivedInARow = 0
    });
    var second = simulator.Simulate(pack, new SimulationRequest
    {
        LevelId = level,
        RunSeed = seed,
        WeatherSeed = Math.Max(seed - 1, 0),
        IsChallengeFile = false,
        ConnectedPlayersOnServer = 0,
        DaysPlayersSurvivedInARow = 0
    });
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
        Path.Combine(ResolveExportRoot(options), $"{version}_{levelId}_{seedStart}_{seedEnd}.db"));

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

    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
    if (File.Exists(output))
    {
        File.Delete(output);
    }
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = output,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    using (var pragma = connection.CreateCommand())
    {
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
        pragma.ExecuteNonQuery();
    }

    using (var create = connection.CreateCommand())
    {
        create.CommandText = """
            CREATE TABLE seeds (
                seed INTEGER PRIMARY KEY,
                run_seed INTEGER NOT NULL,
                weather_seed INTEGER NOT NULL,
                weather TEXT NOT NULL,
                scrap_count INTEGER NOT NULL,
                total_scrap_value INTEGER NOT NULL,
                goldbar_only INTEGER NOT NULL,
                inside_enemy_rolls INTEGER NOT NULL,
                outside_enemy_rolls INTEGER NOT NULL,
                daytime_enemy_rolls INTEGER NOT NULL,
                first_inside_spawn_time TEXT,
                first_outside_spawn_time TEXT,
                first_daytime_spawn_time TEXT,
                estimated_outside_hazards INTEGER NOT NULL,
                power_off_at_start INTEGER NOT NULL,
                key_count INTEGER NOT NULL,
                dungeon_seed INTEGER NOT NULL,
                dungeon_flow_id INTEGER NOT NULL,
                dungeon_flow_name TEXT NOT NULL,
                dungeon_flow_theme TEXT NOT NULL,
                apparatus_spawned INTEGER NOT NULL,
                apparatus_value INTEGER NOT NULL,
                rolls_json TEXT
            );
            CREATE TABLE seed_item_counts (
                seed INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                item_count INTEGER NOT NULL,
                PRIMARY KEY (seed, item_id)
            );
            """;
        create.ExecuteNonQuery();
    }

    using var tx = connection.BeginTransaction();
    using var insertSeed = connection.CreateCommand();
    insertSeed.Transaction = tx;
    insertSeed.CommandText = """
        INSERT INTO seeds (
            seed, run_seed, weather_seed, weather, scrap_count, total_scrap_value, goldbar_only,
            inside_enemy_rolls, outside_enemy_rolls, daytime_enemy_rolls,
            first_inside_spawn_time, first_outside_spawn_time, first_daytime_spawn_time,
            estimated_outside_hazards, power_off_at_start, key_count, dungeon_seed, dungeon_flow_id,
            dungeon_flow_name, dungeon_flow_theme, apparatus_spawned, apparatus_value, rolls_json
        ) VALUES (
            $seed, $run_seed, $weather_seed, $weather, $scrap_count, $total_scrap_value, $goldbar_only,
            $inside_enemy_rolls, $outside_enemy_rolls, $daytime_enemy_rolls,
            $first_inside_spawn_time, $first_outside_spawn_time, $first_daytime_spawn_time,
            $estimated_outside_hazards, $power_off_at_start, $key_count, $dungeon_seed, $dungeon_flow_id,
            $dungeon_flow_name, $dungeon_flow_theme, $apparatus_spawned, $apparatus_value, $rolls_json
        );
        """;
    insertSeed.Parameters.Add("$seed", SqliteType.Integer);
    insertSeed.Parameters.Add("$run_seed", SqliteType.Integer);
    insertSeed.Parameters.Add("$weather_seed", SqliteType.Integer);
    insertSeed.Parameters.Add("$weather", SqliteType.Text);
    insertSeed.Parameters.Add("$scrap_count", SqliteType.Integer);
    insertSeed.Parameters.Add("$total_scrap_value", SqliteType.Integer);
    insertSeed.Parameters.Add("$goldbar_only", SqliteType.Integer);
    insertSeed.Parameters.Add("$inside_enemy_rolls", SqliteType.Integer);
    insertSeed.Parameters.Add("$outside_enemy_rolls", SqliteType.Integer);
    insertSeed.Parameters.Add("$daytime_enemy_rolls", SqliteType.Integer);
    insertSeed.Parameters.Add("$first_inside_spawn_time", SqliteType.Text);
    insertSeed.Parameters.Add("$first_outside_spawn_time", SqliteType.Text);
    insertSeed.Parameters.Add("$first_daytime_spawn_time", SqliteType.Text);
    insertSeed.Parameters.Add("$estimated_outside_hazards", SqliteType.Integer);
    insertSeed.Parameters.Add("$power_off_at_start", SqliteType.Integer);
    insertSeed.Parameters.Add("$key_count", SqliteType.Integer);
    insertSeed.Parameters.Add("$dungeon_seed", SqliteType.Integer);
    insertSeed.Parameters.Add("$dungeon_flow_id", SqliteType.Integer);
    insertSeed.Parameters.Add("$dungeon_flow_name", SqliteType.Text);
    insertSeed.Parameters.Add("$dungeon_flow_theme", SqliteType.Text);
    insertSeed.Parameters.Add("$apparatus_spawned", SqliteType.Integer);
    insertSeed.Parameters.Add("$apparatus_value", SqliteType.Integer);
    insertSeed.Parameters.Add("$rolls_json", SqliteType.Text);

    using var insertItem = connection.CreateCommand();
    insertItem.Transaction = tx;
    insertItem.CommandText = """
        INSERT INTO seed_item_counts (seed, item_id, item_name, item_count)
        VALUES ($seed, $item_id, $item_name, $item_count);
        """;
    insertItem.Parameters.Add("$seed", SqliteType.Integer);
    insertItem.Parameters.Add("$item_id", SqliteType.Integer);
    insertItem.Parameters.Add("$item_name", SqliteType.Text);
    insertItem.Parameters.Add("$item_count", SqliteType.Integer);

    var started = DateTime.UtcNow;
    for (var seed = seedStart; seed <= seedEnd; seed++)
    {
        var report = simulator.Simulate(pack, new SimulationRequest
        {
            LevelId = levelId,
            RunSeed = seed,
            WeatherSeed = Math.Max(seed - 1, 0),
            IsChallengeFile = false,
            ConnectedPlayersOnServer = 0,
            DaysPlayersSurvivedInARow = 0
        });
        insertSeed.Parameters["$seed"].Value = seed;
        insertSeed.Parameters["$run_seed"].Value = report.RunSeed;
        insertSeed.Parameters["$weather_seed"].Value = report.WeatherSeed;
        insertSeed.Parameters["$weather"].Value = report.Weather;
        insertSeed.Parameters["$scrap_count"].Value = report.ScrapCount;
        insertSeed.Parameters["$total_scrap_value"].Value = report.TotalScrapValue;
        insertSeed.Parameters["$goldbar_only"].Value = report.ScrapRolls.All(x => string.Equals(x.ItemName, "Gold bar", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        insertSeed.Parameters["$inside_enemy_rolls"].Value = report.EnemySpawn.Inside.Count;
        insertSeed.Parameters["$outside_enemy_rolls"].Value = report.EnemySpawn.Outside.Count;
        insertSeed.Parameters["$daytime_enemy_rolls"].Value = report.EnemySpawn.Daytime.Count;
        insertSeed.Parameters["$first_inside_spawn_time"].Value = report.EnemySpawn.Inside.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        insertSeed.Parameters["$first_outside_spawn_time"].Value = report.EnemySpawn.Outside.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        insertSeed.Parameters["$first_daytime_spawn_time"].Value = report.EnemySpawn.Daytime.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        insertSeed.Parameters["$estimated_outside_hazards"].Value = report.HazardProp.EstimatedOutsideHazards;
        insertSeed.Parameters["$power_off_at_start"].Value = report.HazardProp.PowerOffAtStart ? 1 : 0;
        insertSeed.Parameters["$key_count"].Value = report.Keys.KeyCount;
        insertSeed.Parameters["$dungeon_seed"].Value = report.Keys.DungeonSeed;
        insertSeed.Parameters["$dungeon_flow_id"].Value = report.Keys.DungeonFlowId;
        insertSeed.Parameters["$dungeon_flow_name"].Value = report.Keys.DungeonFlowName;
        insertSeed.Parameters["$dungeon_flow_theme"].Value = report.Keys.DungeonFlowTheme;
        insertSeed.Parameters["$apparatus_spawned"].Value = report.Apparatus.SpawnedFromSyncedProps ? 1 : 0;
        insertSeed.Parameters["$apparatus_value"].Value = report.Apparatus.Value;
        insertSeed.Parameters["$rolls_json"].Value = includeRolls ? JsonSerializer.Serialize(report.ScrapRolls) : string.Empty;
        insertSeed.ExecuteNonQuery();

        var groupedItems = report.ScrapRolls
            .GroupBy(x => new { x.ItemId, x.ItemName })
            .Select(x => new { x.Key.ItemId, x.Key.ItemName, Count = x.Count() });
        foreach (var item in groupedItems)
        {
            insertItem.Parameters["$seed"].Value = seed;
            insertItem.Parameters["$item_id"].Value = item.ItemId;
            insertItem.Parameters["$item_name"].Value = item.ItemName;
            insertItem.Parameters["$item_count"].Value = item.Count;
            insertItem.ExecuteNonQuery();
        }

        if ((seed - seedStart + 1) % reportInterval == 0)
        {
            var done = seed - seedStart + 1;
            var elapsed = DateTime.UtcNow - started;
            var perSecond = done / Math.Max(elapsed.TotalSeconds, 0.001);
            var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
            Console.WriteLine(
                $"Processed {done:N0} rows, {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
        }
    }

    tx.Commit();
    using (var index = connection.CreateCommand())
    {
        index.CommandText = """
            CREATE INDEX idx_seeds_total_scrap_value ON seeds(total_scrap_value DESC);
            CREATE INDEX idx_seeds_scrap_count ON seeds(scrap_count DESC);
            CREATE INDEX idx_seeds_weather ON seeds(weather);
            CREATE INDEX idx_seeds_key_count ON seeds(key_count DESC);
            CREATE INDEX idx_seeds_dungeon_flow ON seeds(dungeon_flow_theme, dungeon_flow_id);
            CREATE INDEX idx_seed_item_counts_item ON seed_item_counts(item_id, item_count DESC);
            """;
        index.ExecuteNonQuery();
    }

    Console.WriteLine($"SQLite export complete: {output}");
    Console.WriteLine("Example queries:");
    Console.WriteLine("  SELECT seed, total_scrap_value FROM seeds ORDER BY total_scrap_value DESC LIMIT 50;");
    Console.WriteLine("  SELECT seed, weather, key_count FROM seeds WHERE dungeon_flow_theme='Factory' ORDER BY seed LIMIT 100;");
    Console.WriteLine("  SELECT seed, item_count FROM seed_item_counts WHERE item_name='Gold bar' ORDER BY item_count DESC, seed LIMIT 100;");
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
    Console.WriteLine("  inspect --version <key> --seed <n> [--run-seed <n>] [--weather-seed <n>] [--players <n>] [--streak-days <n>] [--moon <idOrName>] [--rules-root <path>]");
    Console.WriteLine("  search --version <key> --seed-start <a> --seed-end <b> [--moon <idOrName>] [--query <expr>] [--threads <n>] [--jsonl true] [--rules-root <path>]");
    Console.WriteLine("  validate --version <key> [--moon <idOrName>] [--seed <n>] [--rules-root <path>]");
    Console.WriteLine("  export-all --version <key> [--moon <idOrName>] [--seed-start <n>] [--seed-end <n>] [--output <db>] [--export-root <path>] [--report-interval <n>] [--include-rolls-json true] [--rules-root <path>]");
    Console.WriteLine("  versions");
}

