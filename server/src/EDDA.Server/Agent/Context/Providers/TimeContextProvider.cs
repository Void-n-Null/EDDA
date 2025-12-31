namespace EDDA.Server.Agent.Context.Providers;

/// <summary>
/// Provides current time and date context.
/// This is always included as core context for time-aware responses.
/// </summary>
public class TimeContextProvider : IContextProvider
{
    public string Key => "time_context";
    public int Priority => 0; // First in prompt

    public Task<string?> GetContextAsync(ContextRequest request, CancellationToken ct = default)
    {
        // Use the system's local time, rather than the raw request.Now (which might be UTC)
        var localNow = request.Now.Kind == DateTimeKind.Local
            ? request.Now
            : request.Now.ToLocalTime();

        var timeZone = TimeZoneInfo.Local;
        var timeZoneAbbr = GetTimeZoneAbbreviation(timeZone, localNow);

        var timeOfDay = localNow.Hour switch
        {
            < 5 => "late night",
            < 12 => "morning",
            < 17 => "afternoon",
            < 21 => "evening",
            _ => "night"
        };

        var context = $"""
            - **Time**: {localNow:h:mm tt} {timeZoneAbbr} ({timeOfDay})
            - **Date**: {localNow:dddd, MMMM d, yyyy}
            """;

        return Task.FromResult<string?>(context);
    }

    // Returns an abbreviation for the system timezone, e.g., "PDT"
    private static string GetTimeZoneAbbreviation(TimeZoneInfo tz, DateTime dt)
    {
        // If Windows/Linux doesn't reliably give abbreviations, fallback to offset
        var baseAbbr = tz.IsDaylightSavingTime(dt)
            ? tz.DaylightName
            : tz.StandardName;

        // Try to condense the timezone name to capital letters for abbreviation
        var abbr = string.Concat(
            baseAbbr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(word => char.IsLetter(word[0]))
                .Select(word => word[0]))
            .ToUpper();

        // If the abbreviation is 'C' (for Central, as in "Central Daylight Time"), try to include a second letter for clarity
        if (abbr.Length < 2 && baseAbbr.Length > 3)
            abbr = baseAbbr[..3].ToUpper();

        // Fallback: display the UTC offset if abbreviation is missing
        if (string.IsNullOrWhiteSpace(abbr))
            abbr = $"UTC{tz.GetUtcOffset(dt):hh\\:mm}";

        return abbr;
    }
}
