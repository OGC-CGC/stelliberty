using Stelliberty.Application.Localization;

namespace Stelliberty.Presentation.Formatting;

public static class RelativeTime
{
    public static string Format(DateTimeOffset? timestamp, ILocalizationService? localization)
    {
        string Localize(string key) => localization?.GetString(key) ?? key;

        if (timestamp is not { } updatedAt)
        {
            return Localize("Common.NotUpdated");
        }

        var elapsed = DateTimeOffset.UtcNow - updatedAt;
        if (elapsed.TotalMinutes < 1)
        {
            return Localize("Common.RelativeTime.JustNow");
        }

        var (count, unit) = elapsed.TotalHours < 1
            ? ((int)elapsed.TotalMinutes, "Minute")
            : elapsed.TotalDays < 1
                ? ((int)elapsed.TotalHours, "Hour")
                : ((int)elapsed.TotalDays, "Day");
        var key = $"Common.RelativeTime.{unit}{(count == 1 ? string.Empty : "s")}Ago";
        return string.Format(Localize(key), count);
    }
}
