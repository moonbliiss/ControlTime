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

public sealed record Settings(string ChildSid, SessionPlan[] Sessions, QuietHours[] QuietHours,
    Guid ResetRequest, string TimeZoneId = "UTC")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId) || string.IsNullOrWhiteSpace(ChildSid) || Sessions is null || Sessions.Length is < 1 or > 24
            || QuietHours is null || QuietHours.Length > 168
            || Sessions.Any(s => s is null || s.UseMinutes is < 1 or > 1440 || s.BreakMinutes is < 0 or > 1440)
            || QuietHours.Any(s => s is null || !Enum.IsDefined(s.StartDay)
                || s.StartMinute is < 0 or >= 1440 || s.EndMinute is < 0 or >= 1440))
            throw new InvalidDataException("Проверьте длительность занятий и расписание.");
    }

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

public sealed record Usage(DateOnly Day, Guid ResetRequest, int Session, int UsedSeconds,
    DateTimeOffset? FinishedAt);
public sealed record Decision(Usage Usage, bool Allowed, string Reason);

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
