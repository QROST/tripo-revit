using System.Globalization;
using System.Text.RegularExpressions;

namespace Tripo.Catalog;

public static class CatalogDimensionParser
{
    private const string Number = @"(?:\d+\s+\d+\s*/\s*\d+|\d+\s*/\s*\d+|\d+(?:[.,]\d+)?)";
    private const string Unit = "(?:millimet(?:er|re)s?|centimet(?:er|re)s?|inches|inch|mm|cm|in|m|[\"″])";
    private static readonly Regex Dimensions = new(
        @"(?<![\p{L}\d])(?<label>(?:(?:overall|total)\s+)?(?:seat\s+(?:width|depth|height)|arm(?:rest)?\s+height|width|depth|height|diameter)|[WDH])\s*(?:\((?<labelUnit>" + Unit + @")\))?\s*[:=]?\s*(?<min>" + Number + @")\s*(?<firstUnit>" + Unit + @")?\s*(?:[-–—]\s*(?<max>" + Number + @")\s*)?(?<unit>" + Unit + @")?(?![\p{L}\d])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<CatalogDimension> Parse(
        string text, CatalogSourceReference? source = null, string? defaultUnit = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = NormalizeFractions(text.Replace('\u00a0', ' '));
        List<CatalogDimension> results = [];
        foreach (Match match in Dimensions.Matches(normalized))
        {
            string label = match.Groups["label"].Value.ToLowerInvariant();
            label = Regex.Replace(label, @"^(overall|total)\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            CatalogDimensionKind kind = label switch
            {
                "w" or "width" => CatalogDimensionKind.Width,
                "d" or "depth" => CatalogDimensionKind.Depth,
                "h" or "height" => CatalogDimensionKind.Height,
                "seat width" => CatalogDimensionKind.SeatWidth,
                "seat depth" => CatalogDimensionKind.SeatDepth,
                "seat height" => CatalogDimensionKind.SeatHeight,
                "arm height" or "armrest height" => CatalogDimensionKind.ArmHeight,
                "diameter" => CatalogDimensionKind.Diameter,
                _ => CatalogDimensionKind.Unknown,
            };
            string unit = match.Groups["unit"].Value;
            if (unit.Length == 0) unit = match.Groups["firstUnit"].Value;
            if (unit.Length == 0) unit = match.Groups["labelUnit"].Value;
            if (unit.Length == 0) unit = defaultUnit ?? "";
            double? multiplier = UnitMultiplier(unit);
            double minimum = ParseNumber(match.Groups["min"].Value);
            double maximum = match.Groups["max"].Success ? ParseNumber(match.Groups["max"].Value) : minimum;
            // Mixed units within a range need explicit review, not silent conversion.
            if (match.Groups["firstUnit"].Success && match.Groups["unit"].Success &&
                UnitMultiplier(match.Groups["firstUnit"].Value) != UnitMultiplier(match.Groups["unit"].Value)) multiplier = null;
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum <= 0 || maximum < minimum) multiplier = null;
            results.Add(new CatalogDimension
            {
                Kind = kind, RawText = match.Value.Trim(),
                MinimumMillimeters = multiplier * minimum, MaximumMillimeters = multiplier * maximum,
                Source = source ?? new CatalogSourceReference { Text = text },
            });
        }
        return results;
    }

    internal static double? UnitMultiplier(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" or "mmt" => 1,
        "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres" or "cmt" => 10,
        "m" or "mtr" => 1000,
        "in" or "inch" or "inches" or "\"" or "″" or "inh" => 25.4,
        _ => null,
    };

    private static double ParseNumber(string value)
    {
        string[] parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!value.Contains('/')) return double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
        string compact = Regex.Replace(value, @"\s*/\s*", "/", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        parts = compact.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] fraction = parts[^1].Split('/');
        double divisor = double.Parse(fraction[1], CultureInfo.InvariantCulture);
        if (divisor == 0) return double.NaN;
        return (parts.Length > 1 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : 0) +
            double.Parse(fraction[0], CultureInfo.InvariantCulture) / divisor;
    }

    private static string NormalizeFractions(string text)
    {
        (char Character, string Value)[] fractions = [('¼', "1/4"), ('½', "1/2"), ('¾', "3/4"),
            ('⅛', "1/8"), ('⅜', "3/8"), ('⅝', "5/8"), ('⅞', "7/8"), ('⅓', "1/3"), ('⅔', "2/3")];
        foreach ((char character, string value) in fractions) text = text.Replace(character.ToString(), " " + value, StringComparison.Ordinal);
        return text;
    }
}
