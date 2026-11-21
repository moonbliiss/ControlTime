using System.Text.Json;
using ControlTime;

var zone = TimeZoneInfo.Utc;
var start = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
var settings = new Settings("child", [new(60, 180), new(60, 0)], [], Guid.Empty);
settings.Validate();

Decision Check(Usage? usage, DateTimeOffset now, bool active = false) =>
    Policy.Evaluate(settings, usage, now, zone, active);

Usage? state = null;
for (int second = 0; second < 3600; second++)
{
    var decision = Check(state, start.AddSeconds(second), true);
    Expect(decision.Allowed, "full first hour is granted");
    state = decision.Usage;
}

var persisted = JsonSerializer.Deserialize<Usage>(JsonSerializer.Serialize(state));
var breakStart = start.AddHours(1);
Expect(!Check(persisted, breakStart, true).Allowed, "limit survives serialized restart");
Expect(!Check(persisted, breakStart.AddHours(2)).Allowed, "one hour of break remains after two parent hours");
Expect(!Check(persisted, breakStart.AddHours(3).AddSeconds(-1)).Allowed, "break remains enforced until deadline");
var available = Check(persisted, breakStart.AddHours(3));
Expect(available.Allowed && available.Usage is { Session: 1, UsedSeconds: 0 }, "second hour is available in full");
Expect(Check(available.Usage, breakStart.AddHours(4)).Usage.UsedSeconds == 0, "parent work does not consume child time");

var lastSecond = available.Usage with { UsedSeconds = 3599 };
var lastGrant = Check(lastSecond, breakStart.AddHours(4), true);
Expect(lastGrant.Allowed, "last second is granted");
Expect(!Check(lastGrant.Usage, breakStart.AddHours(4).AddSeconds(1), true).Allowed, "last session exhausts the day");
Expect(Check(lastGrant.Usage, start.AddDays(1)).Usage is { Session: 0, UsedSeconds: 0 }, "next day resets usage");

settings = settings with { QuietHours = [new(DayOfWeek.Monday, 20 * 60, 10 * 60)] };
Expect(!Check(null, start.AddHours(10)).Allowed, "quiet hours include the starting minute");
Expect(!Check(null, start.AddDays(1).AddMinutes(-1)).Allowed, "overnight prohibition survives midnight");
Expect(Check(null, start.AddDays(1)).Allowed, "quiet hours exclude ending minute");
Expect(Check(null, start.AddDays(1).AddHours(10)).Allowed, "one-day rule does not repeat the next evening");
var blocked = Check(null, start.AddHours(10), true);
Expect(blocked.Usage.UsedSeconds == 0, "prohibition does not consume usage");
Expect(new QuietHours(DayOfWeek.Monday, 600, 600).Contains(start.AddDays(1).AddSeconds(-1).DateTime),
    "equal endpoints produce a full day");
Expect(!new QuietHours(DayOfWeek.Monday, 600, 600).Contains(start.AddDays(1).DateTime),
    "full-day prohibition ends after 24 local hours");

settings = settings with { ResetRequest = Guid.NewGuid() };
Expect(Check(lastGrant.Usage, start.AddHours(5)).Usage is { Session: 0, UsedSeconds: 0 }, "parent reset clears usage");
Console.WriteLine("Policy checks passed (first and second hour, restart, breaks, parent mode and quiet hours).");

static void Expect(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}
