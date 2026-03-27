using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public static class SeedQuery
{
    public static Func<SeedReport, bool> Compile(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _ => true;
        }

        var terms = query
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTerm)
            .ToList();

        return report => terms.All(term => term(report));
    }

    private static Func<SeedReport, bool> ParseTerm(string term)
    {
        if (string.Equals(term, "only-goldbar-day", StringComparison.OrdinalIgnoreCase))
        {
            return report => report.ScrapRolls.All(x => x.ItemId == 152767);
        }

        var split = term.Split('=', 2, StringSplitOptions.TrimEntries);
        if (split.Length != 2)
        {
            throw new InvalidOperationException($"Invalid query term: '{term}'.");
        }

        var key = split[0].ToLowerInvariant();
        var value = split[1];
        return key switch
        {
            "only-item" => OnlyItem(value),
            "contains-item" => ContainsItem(value),
            "min-total-value" => MinTotalValue(value),
            "weather-is" => WeatherIs(value),
            "scrap-count-range" => ScrapCountRange(value),
            _ => throw new InvalidOperationException($"Unknown query key '{key}'.")
        };
    }

    private static Func<SeedReport, bool> OnlyItem(string raw)
    {
        var id = int.Parse(raw);
        return report => report.ScrapRolls.All(x => x.ItemId == id);
    }

    private static Func<SeedReport, bool> ContainsItem(string raw)
    {
        var id = int.Parse(raw);
        return report => report.ScrapRolls.Any(x => x.ItemId == id);
    }

    private static Func<SeedReport, bool> MinTotalValue(string raw)
    {
        var min = int.Parse(raw);
        return report => report.TotalScrapValue >= min;
    }

    private static Func<SeedReport, bool> WeatherIs(string weather) =>
        report => string.Equals(report.Weather, weather, StringComparison.OrdinalIgnoreCase);

    private static Func<SeedReport, bool> ScrapCountRange(string raw)
    {
        var split = raw.Split("..", 2, StringSplitOptions.TrimEntries);
        if (split.Length != 2)
        {
            throw new InvalidOperationException("scrap-count-range must be min..max");
        }

        var min = int.Parse(split[0]);
        var max = int.Parse(split[1]);
        return report => report.ScrapCount >= min && report.ScrapCount <= max;
    }
}
