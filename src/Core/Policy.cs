namespace ControlTime;

public sealed record SessionPlan(int UseMinutes, int BreakMinutes);
public sealed record QuietHours(DayOfWeek StartDay, int StartMinute, int EndMinute)
{
    public bool Contains(DateTime local)
    {
        int minute = local.Hour * 60 + local.Minute;
        if (StartMinute < EndMinute)
            return local.DayOfWeek == StartDay && minute >= StartMinute && minute < EndMinute;

        return (local.DayOfWeek == StartDay && minute >= StartMinute)
            || (local.AddDays(-1).DayOfWeek == StartDay && minute < EndMinute);
    }
}

public sealed record Scenario(Guid Id, string Name, SessionPlan[] Sessions)
{
    public override string ToString() => Name;
}

public sealed record Settings(SessionPlan[] Sessions, QuietHours[] QuietHours,
    Guid ResetRequest, string TimeZoneId = "UTC", Scenario[]? Scenarios = null, Guid? ActiveScenarioId = null,
    bool ShutDownOnLimit = true, int? DailyShutdownMinute = null)
{
    public const int MaxScenarios = 20;
    public const int MaxSessions = 24;

    public Scenario[] GetScenarios() => Scenarios ?? [new(Guid.Empty, "Мой сценарий", Sessions)];
    public Guid GetActiveScenarioId() => ActiveScenarioId ?? Guid.Empty;

    public void Validate()
    {
        var scenarios = GetScenarios();
        if (scenarios.Length is < 1 or > MaxScenarios
            || scenarios.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 48
                || s.Sessions is null || s.Sessions.Length > MaxSessions
                || s.Sessions.Any(plan => plan is null || plan.UseMinutes is < 1 or > 1440
                    || plan.BreakMinutes is < 0 or > 1440))
            || scenarios.Select(s => s.Id).Distinct().Count() != scenarios.Length
            || scenarios.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scenarios.Length
            || !scenarios.Any(s => s.Id == GetActiveScenarioId()))
            throw new InvalidDataException("Проверьте названия сценариев, активный сценарий и длительность занятий.");

        if (string.IsNullOrWhiteSpace(TimeZoneId) || DailyShutdownMinute is < 0 or >= 1440
            || QuietHours is null || QuietHours.Length > 168
            || QuietHours.Any(s => s is null || !Enum.IsDefined(s.StartDay)
                || s.StartMinute is < 0 or >= 1440 || s.EndMinute is < 0 or >= 1440))
            throw new InvalidDataException("Проверьте часовой пояс, запретные часы и время выключения.");
    }

    public Settings WithActiveSessions()
    {
        Validate();
        var active = GetScenarios().Single(s => s.Id == GetActiveScenarioId());
        return this with { Sessions = active.Sessions };
    }

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

public sealed record Usage(DateOnly Day, Guid ResetRequest, int Session, int UsedSeconds,
    DateTimeOffset? FinishedAt);
public sealed record Decision(Usage Usage, bool Allowed, string Reason, bool ForceShutdown = false);

public static class Policy
{
    public static Decision Evaluate(Settings settings, Usage? previous, DateTimeOffset now,
        TimeZoneInfo zone, bool childUsingComputer)
    {
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateOnly today = DateOnly.FromDateTime(local);
        Usage usage = previous is null || today > previous.Day || previous.ResetRequest != settings.ResetRequest
            ? new Usage(today, settings.ResetRequest, 0, 0, null)
            : previous;

        if (settings.DailyShutdownMinute is int cutoff && local.Hour * 60 + local.Minute >= cutoff)
            return new Decision(usage, false, "Наступило время ежедневного выключения.", ForceShutdown: true);

        if (settings.Sessions.Length == 0)
            return settings.QuietHours.Any(window => window.Contains(local))
                ? new Decision(usage, false, "Сейчас действует запрет по расписанию.")
                : new Decision(usage, true, "");

        while (usage.Session < settings.Sessions.Length)
        {
            var session = settings.Sessions[usage.Session];
            if (usage.UsedSeconds < session.UseMinutes * 60 && usage.FinishedAt is null)
                break;

            if (usage.Session == settings.Sessions.Length - 1)
            {
                if (usage.FinishedAt is null)
                    usage = usage with { FinishedAt = now };
                return new Decision(usage, false, "Занятия на сегодня закончились.");
            }

            DateTimeOffset finished = usage.FinishedAt ?? now;
            if (now < finished.AddMinutes(session.BreakMinutes))
                return new Decision(usage with { FinishedAt = finished }, false,
                    "Перерыв до " + TimeZoneInfo.ConvertTime(finished.AddMinutes(session.BreakMinutes), zone).ToString("HH:mm") + ".");

            usage = usage with { Session = usage.Session + 1, UsedSeconds = 0, FinishedAt = null };
        }

        if (usage.Session >= settings.Sessions.Length)
            return new Decision(usage, false, "Занятия на сегодня закончились.");

        if (settings.QuietHours.Any(window => window.Contains(local)))
            return new Decision(usage, false, "Сейчас действует запрет по расписанию.");

        // Reserve the next second before the child can use it.
        if (childUsingComputer)
        {
            int seconds = usage.UsedSeconds + 1;
            DateTimeOffset? finished = seconds == settings.Sessions[usage.Session].UseMinutes * 60
                ? now.AddSeconds(1)
                : null;
            usage = usage with { UsedSeconds = seconds, FinishedAt = finished };
        }

        return new Decision(usage, true, "");
    }
}
