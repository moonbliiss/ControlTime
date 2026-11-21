namespace ControlTime;

public sealed class AccessMode
{
    public const int LoginGraceSeconds = 30;

    private int? session;
    private bool hadChildAccess;
    private long? deadline;
    public bool IsParent { get; private set; }

    public void UpdateSession(int? activeSession)
    {
        if (session == activeSession)
            return;

        session = activeSession;
        if (IsParent)
            UseChildMode();
        if (deadline is null)
            hadChildAccess = false;
    }

    public void UseParentMode()
    {
        IsParent = true;
        hadChildAccess = false;
        deadline = null;
    }

    public void UseChildMode()
    {
        if (!IsParent)
            return;

        IsParent = false;
        hadChildAccess = false;
        deadline = null;
    }

    public int? SecondsBeforeShutdown(bool childAllowed, long monotonicMilliseconds)
    {
        if (session is null || IsParent)
            return null;

        if (childAllowed)
        {
            hadChildAccess = true;
            deadline = null;
            return null;
        }

        deadline ??= monotonicMilliseconds + (hadChildAccess ? 0 : LoginGraceSeconds * 1000);
        long remaining = Math.Max(0, deadline.Value - monotonicMilliseconds);
        return (int)((remaining + 999) / 1000);
    }
}
