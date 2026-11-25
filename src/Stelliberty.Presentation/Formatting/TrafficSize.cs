namespace Stelliberty.Presentation.Formatting;

public static class TrafficSize
{
    private static readonly string[] Prefixes = ["", "K", "M", "G", "T", "P", "E"];

    public static string Format(long bytes, bool displayInBytes) =>
        Format((decimal)bytes, displayInBytes, compact: false);

    public static string FormatRate(long bytesPerSecond, bool displayInBytes) =>
        $"{Format(bytesPerSecond, displayInBytes)}/s";

    public static string FormatAxisRate(double bytesPerSecond, bool displayInBytes) =>
        Format((decimal)bytesPerSecond, displayInBytes, compact: true);

    private static string Format(decimal bytes, bool displayInBytes, bool compact)
    {
        var value = displayInBytes ? bytes : bytes * 8;
        var unit = 0;
        while (value >= 1024 && unit < Prefixes.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var prefix = Prefixes[unit];
        if (compact)
        {
            if (displayInBytes)
            {
                return $"{value:0.#}{prefix}";
            }

            var ratePrefix = unit == 1 ? "k" : prefix;
            return $"{value:0.#} {ratePrefix}bps";
        }

        var unitName = displayInBytes ? "B" : "b";
        return unit == 0 ? $"{value:0.#} {unitName}" : $"{value:0.#} {prefix}i{unitName}";
    }
}
