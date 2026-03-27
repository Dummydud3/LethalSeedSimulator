using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class EnemySpawnSimulator
{
    public EnemySpawnReport Simulate(RulePack rules, LevelRule level, int runSeed)
    {
        var insideRandom = new Random(runSeed + rules.Offsets.EnemySpawnRandom);
        var outsideRandom = new Random(runSeed + rules.Offsets.OutsideEnemySpawnRandom);
        var daytimeRandom = new Random(runSeed + rules.Offsets.EnemySpawnRandom + 1);
        var batchHours = Math.Max(1, (int)Math.Round(rules.GlobalRules.HourTimeBetweenEnemySpawnBatches, MidpointRounding.AwayFromZero));
        var numberOfHoursInDay = Math.Max(1, rules.GlobalRules.NumberOfHoursInDay);
        var lengthOfHours = Math.Max(1f, rules.GlobalRules.LengthOfHours);
        var startCurrentDayTime = CalculateStartCurrentDayTime(level, lengthOfHours, numberOfHoursInDay);
        var spawnBeginHour = CalculateSpawnBeginHour(startCurrentDayTime, lengthOfHours);
        var beginDayTime = startCurrentDayTime + spawnBeginHour * lengthOfHours;
        var cycleHours = BuildCycleHours(numberOfHoursInDay, batchHours);
        var insideAttempts = Math.Max(rules.GlobalRules.MinEnemiesToSpawn, Math.Min(8, Math.Max(1, level.InsideEnemies.Count / 2)));
        var outsideAttempts = Math.Max(rules.GlobalRules.MinOutsideEnemiesToSpawn, Math.Min(6, Math.Max(1, level.OutsideEnemies.Count / 2)));
        var daytimeAttempts = Math.Min(5, Math.Max(1, level.DaytimeEnemies.Count / 2));

        return new EnemySpawnReport
        {
            Inside = RollInside(level.InsideEnemies, insideRandom, insideAttempts, cycleHours, beginDayTime, startCurrentDayTime, lengthOfHours, numberOfHoursInDay),
            Outside = RollCycleAnchored(level.OutsideEnemies, outsideRandom, "outside", outsideAttempts, cycleHours, beginDayTime, startCurrentDayTime, lengthOfHours, numberOfHoursInDay),
            Daytime = RollCycleAnchored(level.DaytimeEnemies, daytimeRandom, "daytime", daytimeAttempts, cycleHours, beginDayTime, startCurrentDayTime, lengthOfHours, numberOfHoursInDay)
        };
    }

    private static List<EnemySpawnRoll> RollInside(
        IReadOnlyList<EnemyRule> pool,
        Random random,
        int attempts,
        IReadOnlyList<int> cycleHours,
        double beginDayTime,
        double startCurrentDayTime,
        float lengthOfHours,
        int numberOfHoursInDay)
    {
        var output = new List<EnemySpawnRoll>();
        if (pool.Count == 0)
        {
            return output;
        }

        var weights = pool.Select(x => x.Rarity).ToArray();
        var hourBatch = cycleHours.Count > 1 ? cycleHours[1] - cycleHours[0] : 2;
        for (var i = 0; i < attempts; i++)
        {
            var idx = WeightedPicker.GetRandomWeightedIndex(weights, random);
            var cycleHour = SpreadCycleHour(i, attempts, cycleHours);
            var cycleBaseDayTime = cycleHour * lengthOfHours;
            var spawnDayTime = (double)random.Next(
                (int)(10f + cycleBaseDayTime),
                (int)(lengthOfHours * hourBatch + cycleBaseDayTime));
            if (spawnDayTime < beginDayTime)
            {
                spawnDayTime = beginDayTime;
            }

            var spawnHour = Math.Round(Math.Max(0.0, (spawnDayTime - startCurrentDayTime) / lengthOfHours), 2);
            output.Add(new EnemySpawnRoll
            {
                EnemyName = pool[idx].Name,
                Category = "inside",
                SpawnHour = spawnHour,
                SpawnMinute = (int)Math.Round(spawnHour * 60.0, MidpointRounding.AwayFromZero),
                SpawnTimeOfDay = FormatHudTimeOfDay(spawnDayTime, lengthOfHours, numberOfHoursInDay)
            });
        }

        return output;
    }

    private static List<EnemySpawnRoll> RollCycleAnchored(
        IReadOnlyList<EnemyRule> pool,
        Random random,
        string category,
        int attempts,
        IReadOnlyList<int> cycleHours,
        double beginDayTime,
        double startCurrentDayTime,
        float lengthOfHours,
        int numberOfHoursInDay)
    {
        var output = new List<EnemySpawnRoll>();
        if (pool.Count == 0)
        {
            return output;
        }

        var weights = pool.Select(x => x.Rarity).ToArray();
        for (var i = 0; i < attempts; i++)
        {
            var idx = WeightedPicker.GetRandomWeightedIndex(weights, random);
            var cycleHour = SpreadCycleHour(i, attempts, cycleHours);
            var spawnDayTime = cycleHour * lengthOfHours;
            if (spawnDayTime < beginDayTime)
            {
                continue;
            }

            var spawnHour = Math.Round(Math.Max(0.0, (spawnDayTime - startCurrentDayTime) / lengthOfHours), 2);
            output.Add(new EnemySpawnRoll
            {
                EnemyName = pool[idx].Name,
                Category = category,
                SpawnHour = spawnHour,
                SpawnMinute = (int)Math.Round(spawnHour * 60.0, MidpointRounding.AwayFromZero),
                SpawnTimeOfDay = FormatHudTimeOfDay(spawnDayTime, lengthOfHours, numberOfHoursInDay)
            });
        }

        return output;
    }

    private static double CalculateStartCurrentDayTime(LevelRule level, float lengthOfHours, int numberOfHoursInDay)
    {
        const float startingGlobalTime = 100f;
        var totalTime = lengthOfHours * numberOfHoursInDay;
        var daySpeed = Math.Max(0.0001f, level.DaySpeedMultiplier);
        return (startingGlobalTime + level.OffsetFromGlobalTime) * daySpeed % (totalTime + 1f);
    }

    private static double CalculateSpawnBeginHour(double startCurrentDayTime, float lengthOfHours)
    {
        const double beginSpawningAtDayTime = 85.0;
        if (startCurrentDayTime >= beginSpawningAtDayTime)
        {
            return 0.0;
        }

        return (beginSpawningAtDayTime - startCurrentDayTime) / Math.Max(1.0, lengthOfHours);
    }

    private static List<int> BuildCycleHours(int numberOfHoursInDay, int batchHours)
    {
        var list = new List<int>();
        var h = 0;
        while (h < numberOfHoursInDay)
        {
            list.Add(h);
            h += Math.Max(1, batchHours);
        }

        return list;
    }

    private static int SpreadCycleHour(int index, int attempts, IReadOnlyList<int> cycleHours)
    {
        if (cycleHours.Count == 0)
        {
            return 0;
        }

        if (attempts <= 1)
        {
            return cycleHours[0];
        }

        var t = (double)index / (attempts - 1);
        var mapped = (int)Math.Round(t * (cycleHours.Count - 1), MidpointRounding.AwayFromZero);
        return cycleHours[Math.Clamp(mapped, 0, cycleHours.Count - 1)];
    }

    private static string FormatHudTimeOfDay(double currentDayTime, float lengthOfHours, int numberOfHoursInDay)
    {
        var totalTime = Math.Max(1f, lengthOfHours * numberOfHoursInDay);
        var normalized = Math.Max(0.0, currentDayTime / totalTime);
        var totalMinutes = (int)(normalized * (60.0 * numberOfHoursInDay)) + 360;
        var hour = (int)Math.Floor(totalMinutes / 60.0);
        if (hour >= 24)
        {
            return "12:00 AM";
        }

        var amPm = hour < 12 ? "AM" : "PM";
        if (hour > 12)
        {
            hour %= 12;
        }

        var minute = totalMinutes % 60;
        return string.Format("{0:00}:{1:00}", hour, minute).TrimStart('0') + " " + amPm;
    }
}
