using System.Text;
using System.Text.Json;
using LethalSeedSimulator.Core;
using LethalSeedSimulator.Extractor;
using LethalSeedSimulator.Rules;
using Microsoft.Data.Sqlite;

namespace LethalSeedSimulator.Gui;

internal sealed class GuiServices
{
    public sealed class MoonProgressRow
    {
        public required string MoonId { get; init; }

        public required string MoonName { get; init; }

        public long SimulatedCount { get; init; }

        public double Percent { get; init; }
    }

    public sealed class SeedRow
    {
        public int Seed { get; init; }

        public int TotalScrapValue { get; init; }

        public int ScrapCount { get; init; }

        public string Weather { get; init; } = string.Empty;

        public int KeyCount { get; init; }

        public string DungeonFlowTheme { get; init; } = string.Empty;

        public int ApparatusValue { get; init; }
    }

    public sealed class SeedPage
    {
        public required List<SeedRow> Rows { get; init; }

        public int TotalCount { get; init; }
    }

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

    public SeedReport Inspect(string version, string levelId, int runSeed, int weatherSeed, int players, int streakDays)
    {
        var pack = ruleProvider.Load(version);
        return simulator.Simulate(pack, new SimulationRequest
        {
            LevelId = levelId,
            RunSeed = runSeed,
            WeatherSeed = weatherSeed,
            IsChallengeFile = false,
            ConnectedPlayersOnServer = players,
            DaysPlayersSurvivedInARow = streakDays
        });
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
                var report = simulator.Simulate(pack, new SimulationRequest
                {
                    LevelId = levelId,
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

        return hits.OrderBy(x => x.Seed).ToList();
    }

    public void ExportSqlite(
        string version,
        string levelId,
        int seedStart,
        int seedEnd,
        string outputDb,
        int reportInterval,
        bool includeRollsJson,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var pack = ruleProvider.Load(version);
        var level = pack.Levels.FirstOrDefault(x => string.Equals(x.Id, levelId, StringComparison.OrdinalIgnoreCase))
            ?? pack.Levels.FirstOrDefault(x => string.Equals(x.Name, levelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown level id '{levelId}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputDb) ?? ".");
        if (File.Exists(outputDb))
        {
            File.Delete(outputDb);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = outputDb,
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
            cancellationToken.ThrowIfCancellationRequested();
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
            insertSeed.Parameters["$rolls_json"].Value = includeRollsJson ? JsonSerializer.Serialize(report.ScrapRolls) : string.Empty;
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
            var done = seed - seedStart + 1;
            if (done % reportInterval == 0)
            {
                var elapsed = DateTime.UtcNow - started;
                var perSecond = done / Math.Max(elapsed.TotalSeconds, 0.001);
                var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
                progress?.Report($"Processed {done:N0} rows @ {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
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

        progress?.Report($"SQLite export complete: {outputDb}");
    }

    public string GetMoonDbPath(string dataRoot, string version, string levelId)
    {
        var folder = Path.Combine(dataRoot, "data", version, levelId);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "seeds.db");
    }

    public IReadOnlyList<MoonProgressRow> GetMoonProgress(string dataRoot, string version, int maxSeed = 99_999_999)
    {
        var levels = GetLevels(version);
        var rows = new List<MoonProgressRow>(levels.Count);
        foreach (var level in levels)
        {
            var dbPath = GetMoonDbPath(dataRoot, version, level.Id);
            var count = 0L;
            if (File.Exists(dbPath))
            {
                using var connection = OpenConnection(dbPath);
                EnsureMoonDbSchema(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM seeds;";
                count = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            var percent = Math.Min(100.0, (count * 100.0) / (maxSeed + 1L));
            rows.Add(new MoonProgressRow
            {
                MoonId = level.Id,
                MoonName = level.Name,
                SimulatedCount = count,
                Percent = percent
            });
        }

        return rows;
    }

    public void SimulateRangeToMoonDb(
        string dataRoot,
        string version,
        string levelId,
        int seedStart,
        int seedEnd,
        bool forceResimulate,
        int reportInterval,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (seedEnd < seedStart)
        {
            throw new InvalidOperationException("Seed end must be >= seed start.");
        }

        var pack = ruleProvider.Load(version);
        _ = pack.Levels.FirstOrDefault(x => string.Equals(x.Id, levelId, StringComparison.OrdinalIgnoreCase))
            ?? pack.Levels.FirstOrDefault(x => string.Equals(x.Name, levelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown level id '{levelId}'.");
        var dbPath = GetMoonDbPath(dataRoot, version, levelId);
        using var connection = OpenConnection(dbPath);
        EnsureMoonDbSchema(connection);
        using (var bulkPragma = connection.CreateCommand())
        {
            bulkPragma.CommandText = "PRAGMA cache_size = -200000; PRAGMA journal_size_limit = 67108864; PRAGMA wal_autocheckpoint = 0;";
            bulkPragma.ExecuteNonQuery();
        }
        var tableWasEmpty = true;
        using (var countSeeds = connection.CreateCommand())
        {
            countSeeds.CommandText = "SELECT COUNT(*) FROM seeds;";
            tableWasEmpty = Convert.ToInt64(countSeeds.ExecuteScalar() ?? 0L) == 0L;
        }

        SqliteTransaction tx = connection.BeginTransaction();
        SqliteCommand insertSeed = BuildInsertSeedCommand(connection, tx, forceResimulate);
        SqliteCommand deleteItemsForSeed = BuildDeleteItemsForSeedCommand(connection, tx);
        SqliteCommand insertItem = BuildInsertItemCommand(connection, tx);
        SqliteCommand seedExists = BuildSeedExistsCommand(connection, tx);

        var started = DateTime.UtcNow;
        var inserted = 0;
        var skippedExisting = 0;
        var sinceCommit = 0;
        const int commitEvery = 5000;
        for (var seed = seedStart; seed <= seedEnd; seed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!forceResimulate)
            {
                seedExists.Parameters["$seed"].Value = seed;
                if (seedExists.ExecuteScalar() is not null)
                {
                    skippedExisting++;
                    var skipDone = seed - seedStart + 1;
                    if (skipDone % reportInterval == 0)
                    {
                        var elapsed = DateTime.UtcNow - started;
                        var perSecond = skipDone / Math.Max(elapsed.TotalSeconds, 0.001);
                        var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
                        progress?.Report($"Processed {skipDone:N0} / {(seedEnd - seedStart + 1):N0}, wrote {inserted:N0}, skipped {skippedExisting:N0}, {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
                    }

                    continue;
                }
            }

            var report = simulator.Simulate(pack, new SimulationRequest
            {
                LevelId = levelId,
                RunSeed = seed,
                WeatherSeed = Math.Max(seed - 1, 0),
                IsChallengeFile = false,
                ConnectedPlayersOnServer = 0,
                DaysPlayersSurvivedInARow = 0
            });

            FillSeedParameters(insertSeed, seed, report, includeRollsJson: false);
            var changed = insertSeed.ExecuteNonQuery();
            var wroteSeed = forceResimulate || changed > 0;
            if (!wroteSeed)
            {
                skippedExisting++;
                continue;
            }

            deleteItemsForSeed.Parameters["$seed"].Value = seed;
            deleteItemsForSeed.ExecuteNonQuery();
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

            inserted++;
            sinceCommit++;
            if (sinceCommit >= commitEvery)
            {
                tx.Commit();
                tx.Dispose();
                insertSeed.Dispose();
                deleteItemsForSeed.Dispose();
                insertItem.Dispose();
                seedExists.Dispose();

                tx = connection.BeginTransaction();
                insertSeed = BuildInsertSeedCommand(connection, tx, forceResimulate);
                deleteItemsForSeed = BuildDeleteItemsForSeedCommand(connection, tx);
                insertItem = BuildInsertItemCommand(connection, tx);
                seedExists = BuildSeedExistsCommand(connection, tx);
                sinceCommit = 0;
            }

            var done = seed - seedStart + 1;
            if (done % reportInterval == 0)
            {
                var elapsed = DateTime.UtcNow - started;
                var perSecond = done / Math.Max(elapsed.TotalSeconds, 0.001);
                var remaining = (seedEnd - seed) / Math.Max(perSecond, 0.001);
                progress?.Report($"Processed {done:N0} / {(seedEnd - seedStart + 1):N0}, wrote {inserted:N0}, skipped {skippedExisting:N0}, {perSecond:N0}/sec, ETA {TimeSpan.FromSeconds(remaining):g}");
            }
        }

        tx.Commit();
        tx.Dispose();
        insertSeed.Dispose();
        deleteItemsForSeed.Dispose();
        insertItem.Dispose();
        seedExists.Dispose();
        using (var finalizePragma = connection.CreateCommand())
        {
            finalizePragma.CommandText = "PRAGMA wal_autocheckpoint = 1000;";
            finalizePragma.ExecuteNonQuery();
        }
        if (tableWasEmpty || forceResimulate)
        {
            EnsureMoonDbIndexes(connection);
        }
        progress?.Report($"Simulation complete for moon {levelId}. Rows written: {inserted:N0}. Skipped existing: {skippedExisting:N0}. DB: {dbPath}");
    }

    public SeedPage QueryMoonRows(
        string dataRoot,
        string version,
        string levelId,
        string sortColumn,
        bool sortDescending,
        int? minTotalScrapValue,
        int? maxTotalScrapValue,
        int page,
        int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 5000);
        var dbPath = GetMoonDbPath(dataRoot, version, levelId);
        if (!File.Exists(dbPath))
        {
            return new SeedPage { Rows = [], TotalCount = 0 };
        }

        using var connection = OpenConnection(dbPath);
        EnsureMoonDbSchema(connection);

        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["seed"] = "seed",
            ["total_scrap_value"] = "total_scrap_value",
            ["scrap_count"] = "scrap_count",
            ["key_count"] = "key_count",
            ["apparatus_value"] = "apparatus_value",
            ["weather"] = "weather",
            ["dungeon_flow_theme"] = "dungeon_flow_theme"
        };
        var orderBy = allowed.TryGetValue(sortColumn, out var mapped) ? mapped : "seed";
        var sortDir = sortDescending ? "DESC" : "ASC";

        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*)
            FROM seeds
            WHERE ($min IS NULL OR total_scrap_value >= $min)
              AND ($max IS NULL OR total_scrap_value <= $max);
            """;
        count.Parameters.AddWithValue("$min", minTotalScrapValue is null ? DBNull.Value : minTotalScrapValue);
        count.Parameters.AddWithValue("$max", maxTotalScrapValue is null ? DBNull.Value : maxTotalScrapValue);
        var totalCount = Convert.ToInt32(count.ExecuteScalar() ?? 0);

        var offset = (page - 1) * pageSize;
        using var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT seed, total_scrap_value, scrap_count, weather, key_count, dungeon_flow_theme, apparatus_value
            FROM seeds
            WHERE ($min IS NULL OR total_scrap_value >= $min)
              AND ($max IS NULL OR total_scrap_value <= $max)
            ORDER BY {orderBy} {sortDir}, seed ASC
            LIMIT $limit OFFSET $offset;
            """;
        query.Parameters.AddWithValue("$min", minTotalScrapValue is null ? DBNull.Value : minTotalScrapValue);
        query.Parameters.AddWithValue("$max", maxTotalScrapValue is null ? DBNull.Value : maxTotalScrapValue);
        query.Parameters.AddWithValue("$limit", pageSize);
        query.Parameters.AddWithValue("$offset", offset);

        var rows = new List<SeedRow>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SeedRow
            {
                Seed = reader.GetInt32(0),
                TotalScrapValue = reader.GetInt32(1),
                ScrapCount = reader.GetInt32(2),
                Weather = reader.GetString(3),
                KeyCount = reader.GetInt32(4),
                DungeonFlowTheme = reader.GetString(5),
                ApparatusValue = reader.GetInt32(6)
            });
        }

        return new SeedPage { Rows = rows, TotalCount = totalCount };
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureMoonDbSchema(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS seeds (
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
            CREATE TABLE IF NOT EXISTS seed_item_counts (
                seed INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                item_count INTEGER NOT NULL,
                PRIMARY KEY (seed, item_id)
            );
            """;
        create.ExecuteNonQuery();
    }

    private static void EnsureMoonDbIndexes(SqliteConnection connection)
    {
        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_seeds_total_scrap_value ON seeds(total_scrap_value DESC);
            CREATE INDEX IF NOT EXISTS idx_seeds_scrap_count ON seeds(scrap_count DESC);
            CREATE INDEX IF NOT EXISTS idx_seeds_weather ON seeds(weather);
            CREATE INDEX IF NOT EXISTS idx_seeds_key_count ON seeds(key_count DESC);
            CREATE INDEX IF NOT EXISTS idx_seeds_dungeon_flow ON seeds(dungeon_flow_theme, dungeon_flow_id);
            CREATE INDEX IF NOT EXISTS idx_seed_item_counts_item ON seed_item_counts(item_id, item_count DESC);
            """;
        index.ExecuteNonQuery();
    }

    private static void AddSeedParameters(SqliteCommand insertSeed)
    {
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
    }

    private static SqliteCommand BuildInsertSeedCommand(SqliteConnection connection, SqliteTransaction tx, bool forceResimulate)
    {
        var insertSeed = connection.CreateCommand();
        insertSeed.Transaction = tx;
        insertSeed.CommandText = forceResimulate
            ? """
              INSERT OR REPLACE INTO seeds (
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
              """
            : """
              INSERT OR IGNORE INTO seeds (
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
        AddSeedParameters(insertSeed);
        return insertSeed;
    }

    private static SqliteCommand BuildDeleteItemsForSeedCommand(SqliteConnection connection, SqliteTransaction tx)
    {
        var deleteItemsForSeed = connection.CreateCommand();
        deleteItemsForSeed.Transaction = tx;
        deleteItemsForSeed.CommandText = "DELETE FROM seed_item_counts WHERE seed = $seed;";
        deleteItemsForSeed.Parameters.Add("$seed", SqliteType.Integer);
        return deleteItemsForSeed;
    }

    private static SqliteCommand BuildInsertItemCommand(SqliteConnection connection, SqliteTransaction tx)
    {
        var insertItem = connection.CreateCommand();
        insertItem.Transaction = tx;
        insertItem.CommandText = """
            INSERT OR REPLACE INTO seed_item_counts (seed, item_id, item_name, item_count)
            VALUES ($seed, $item_id, $item_name, $item_count);
            """;
        insertItem.Parameters.Add("$seed", SqliteType.Integer);
        insertItem.Parameters.Add("$item_id", SqliteType.Integer);
        insertItem.Parameters.Add("$item_name", SqliteType.Text);
        insertItem.Parameters.Add("$item_count", SqliteType.Integer);
        return insertItem;
    }

    private static SqliteCommand BuildSeedExistsCommand(SqliteConnection connection, SqliteTransaction tx)
    {
        var seedExists = connection.CreateCommand();
        seedExists.Transaction = tx;
        seedExists.CommandText = "SELECT 1 FROM seeds WHERE seed = $seed LIMIT 1;";
        seedExists.Parameters.Add("$seed", SqliteType.Integer);
        return seedExists;
    }

    private static void FillSeedParameters(SqliteCommand cmd, int seed, SeedReport report, bool includeRollsJson)
    {
        cmd.Parameters["$seed"].Value = seed;
        cmd.Parameters["$run_seed"].Value = report.RunSeed;
        cmd.Parameters["$weather_seed"].Value = report.WeatherSeed;
        cmd.Parameters["$weather"].Value = report.Weather;
        cmd.Parameters["$scrap_count"].Value = report.ScrapCount;
        cmd.Parameters["$total_scrap_value"].Value = report.TotalScrapValue;
        cmd.Parameters["$goldbar_only"].Value = report.ScrapRolls.All(x => string.Equals(x.ItemName, "Gold bar", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        cmd.Parameters["$inside_enemy_rolls"].Value = report.EnemySpawn.Inside.Count;
        cmd.Parameters["$outside_enemy_rolls"].Value = report.EnemySpawn.Outside.Count;
        cmd.Parameters["$daytime_enemy_rolls"].Value = report.EnemySpawn.Daytime.Count;
        cmd.Parameters["$first_inside_spawn_time"].Value = report.EnemySpawn.Inside.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        cmd.Parameters["$first_outside_spawn_time"].Value = report.EnemySpawn.Outside.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        cmd.Parameters["$first_daytime_spawn_time"].Value = report.EnemySpawn.Daytime.FirstOrDefault()?.SpawnTimeOfDay ?? string.Empty;
        cmd.Parameters["$estimated_outside_hazards"].Value = report.HazardProp.EstimatedOutsideHazards;
        cmd.Parameters["$power_off_at_start"].Value = report.HazardProp.PowerOffAtStart ? 1 : 0;
        cmd.Parameters["$key_count"].Value = report.Keys.KeyCount;
        cmd.Parameters["$dungeon_seed"].Value = report.Keys.DungeonSeed;
        cmd.Parameters["$dungeon_flow_id"].Value = report.Keys.DungeonFlowId;
        cmd.Parameters["$dungeon_flow_name"].Value = report.Keys.DungeonFlowName;
        cmd.Parameters["$dungeon_flow_theme"].Value = report.Keys.DungeonFlowTheme;
        cmd.Parameters["$apparatus_spawned"].Value = report.Apparatus.SpawnedFromSyncedProps ? 1 : 0;
        cmd.Parameters["$apparatus_value"].Value = report.Apparatus.Value;
        cmd.Parameters["$rolls_json"].Value = includeRollsJson ? JsonSerializer.Serialize(report.ScrapRolls) : string.Empty;
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

}
