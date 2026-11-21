using System.Diagnostics;
using System.ServiceProcess;

namespace ControlTime;

internal sealed class ControlService : ServiceBase
{
    public const string Name = "ControlTime";
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string? childSid;
    private string? lastError;

    public ControlService()
    {
        ServiceName = Name;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        cancellation = new CancellationTokenSource();
        worker = Task.Run(() => Run(cancellation.Token));
    }

    protected override void OnStop()
    {
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        cancellation?.Dispose();
    }

    private async Task Run(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    Check();
                    lastError = null;
                }
                catch (Exception error)
                {
                    Report(error);
                    if (childSid is { } sid)
                    {
                        try
                        {
                            foreach (var session in Sessions.List().Where(session => session.Sid == sid))
                                Sessions.LogOff(session.Id);
                        }
                        catch (Exception logoffError)
                        {
                            Report(logoffError);
                        }
                    }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void Report(Exception error)
    {
        string description = error.GetType().Name + ": " + error.Message;
        if (description == lastError)
            return;
        lastError = description;
        try
        {
            EventLog.WriteEntry(description, EventLogEntryType.Error);
        }
        catch (Exception)
        {
            // Event logging cannot interrupt enforcement.
        }
    }

    private void Check()
    {
        var settings = Storage.ReadSettings();
        if (settings is null)
        {
            if (childSid is null)
                return;
            throw new InvalidDataException("Настройки ControlTime отсутствуют.");
        }

        childSid = new System.Security.Principal.SecurityIdentifier(settings.ChildSid).Value;
        settings.Validate();
        var sessions = Sessions.List();
        var children = sessions.Where(session => session.Sid == childSid).ToArray();
        var current = Storage.ReadUsage();
        var decision = Policy.Evaluate(settings, current, DateTimeOffset.UtcNow,
            settings.GetTimeZone(), children.Any(session => session.Active));
        if (decision.Usage != current)
            Storage.Save(decision.Usage);

        if (decision.Allowed || children.Length == 0)
            return;

        // Preserve other signed-in sessions and any unsaved work in them.
        if (children.Any(session => session.Active) && Sessions.List().All(session => session.Sid == childSid))
            Sessions.ShutDown();

        foreach (var session in children)
            Sessions.LogOff(session.Id);
    }
}
