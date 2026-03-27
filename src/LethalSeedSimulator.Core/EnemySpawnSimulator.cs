using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class EnemySpawnSimulator
{
    public EnemySpawnReport Simulate(RulePack rules, LevelRule level, int runSeed)
    {
        var insideRandom = new Random(runSeed + rules.Offsets.EnemySpawnRandom);
        var outsideRandom = new Random(runSeed + rules.Offsets.OutsideEnemySpawnRandom);
        var daytimeRandom = new Random(runSeed + rules.Offsets.EnemySpawnRandom + 1);

        return new EnemySpawnReport
        {
            Inside = Roll(level.InsideEnemies, insideRandom, "inside", Math.Min(8, Math.Max(1, level.InsideEnemies.Count / 2))),
            Outside = Roll(level.OutsideEnemies, outsideRandom, "outside", Math.Min(6, Math.Max(1, level.OutsideEnemies.Count / 2))),
            Daytime = Roll(level.DaytimeEnemies, daytimeRandom, "daytime", Math.Min(5, Math.Max(1, level.DaytimeEnemies.Count / 2)))
        };
    }

    private static List<EnemySpawnRoll> Roll(IReadOnlyList<EnemyRule> pool, Random random, string category, int attempts)
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
            output.Add(new EnemySpawnRoll
            {
                EnemyName = pool[idx].Name,
                Category = category
            });
        }

        return output;
    }
}
