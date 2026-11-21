using System.Text.Json;
using ControlTime;

var zone = TimeZoneInfo.Utc;
var start = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
var settings = new Settings([new(60, 180), new(60, 0)], [], Guid.Empty);
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
var mode = new AccessMode();
mode.UpdateSession(1);
Expect(mode.SecondsBeforeShutdown(true, 0) is null, "normal child access");
Expect(mode.SecondsBeforeShutdown(false, 1000) == 0, "shut down when child time ends");
mode.UseParentMode();
Expect(mode.IsParent && mode.SecondsBeforeShutdown(false, 1000) is null, "parent can work during a break");
mode.UpdateSession(2);
Expect(!mode.IsParent, "new Windows sign-in starts in child mode");
Expect(mode.SecondsBeforeShutdown(false, 0) == AccessMode.LoginGraceSeconds,
    "a blocked sign-in gives the parent time to enter the password");
mode.UpdateSession(null);
mode.UpdateSession(3);
Expect(mode.SecondsBeforeShutdown(false, 5000) == AccessMode.LoginGraceSeconds - 5,
    "signing out and back in does not reset an active shutdown countdown");
mode.UseChildMode();
Expect(mode.SecondsBeforeShutdown(false, 10000) == AccessMode.LoginGraceSeconds - 10,
    "requesting child mode again does not reset a shutdown countdown");
Expect(mode.SecondsBeforeShutdown(false, 35000) == 0, "blocked sign-in cannot use more time");
mode.UseParentMode();
mode.UseChildMode();
Expect(!mode.IsParent && mode.SecondsBeforeShutdown(false, 36000) == AccessMode.LoginGraceSeconds,
    "returning to child mode restores the restriction immediately");

string password = "parent password for test";
var firstPassword = ParentPassword.Create(password);
var secondPassword = ParentPassword.Create(password);
Expect(firstPassword.Matches(password) && !firstPassword.Matches("different password"),
    "parent password validation");
Expect(!firstPassword.Salt.SequenceEqual(secondPassword.Salt)
    && !firstPassword.Hash.SequenceEqual(secondPassword.Hash), "passwords use separate random salts");
Expect(!ParentPassword.ValidLength("short"), "reject short password");
var failed = Attempts.Empty.Failed(start);
Expect(failed.RetryAt == start.AddSeconds(2) && failed.Failed(start).RetryAt == start.AddSeconds(4),
    "failed password attempts have increasing cooldowns");
Expect(JsonSerializer.Deserialize<Attempts>(JsonSerializer.Serialize(failed)) == failed,
    "password retry limit persists after restart");
Console.WriteLine("Time policy, same-account modes, password validation and retry checks passed.");

static void Expect(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}
