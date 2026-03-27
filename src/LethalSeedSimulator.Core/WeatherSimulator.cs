using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class WeatherSimulator
{
    public WeatherReport Simulate(RulePack rules, SimulationRequest request)
    {
        var weatherSeed = request.WeatherSeed ?? Math.Max(request.RunSeed - 1, 0);
        var random = new Random(weatherSeed + rules.Offsets.WeatherRandom);

        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in rules.Levels)
        {
            assignments[level.Id] = level.OverrideWeather ? level.OverrideWeatherType : "None";
        }

        var selectable = rules.Levels
            .Where(x => !(x.OverrideWeather && !string.Equals(x.OverrideWeatherType, "None", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var multiplayerModifier = 1f;
        if (request.ConnectedPlayersOnServer + 1 > 1 &&
            request.DaysPlayersSurvivedInARow > 2 &&
            request.DaysPlayersSurvivedInARow % 3 == 0)
        {
            multiplayerModifier = random.Next(15, 25) / 10f;
        }

        var weatherChance = Math.Clamp((float)random.NextDouble() * multiplayerModifier, 0f, 1f);
        var toAssign = Math.Clamp((int)(weatherChance * (rules.Levels.Count - 2f)), 0, rules.Levels.Count);
        for (var i = 0; i < toAssign && selectable.Count > 0; i++)
        {
            var idx = random.Next(0, selectable.Count);
            var level = selectable[idx];
            selectable.RemoveAt(idx);

            if (level.Weathers.Count == 0)
            {
                continue;
            }

            var weatherIndex = random.Next(0, level.Weathers.Count);
            assignments[level.Id] = level.Weathers[weatherIndex].WeatherType;
        }

        return new WeatherReport
        {
            AssignedWeatherByLevelId = assignments
        };
    }
}
