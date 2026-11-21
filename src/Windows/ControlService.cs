using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;

namespace ControlTime;

internal sealed class ControlService : ServiceBase
{
    public const string Name = "ControlTime";
    private readonly object sync = new();
    private readonly AccessMode mode = new();
    private readonly HashSet<int> locked = [];
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Task? listener;
    private string? lastError;
    private bool shutdownRequested;
    private bool configured;
    private Settings? settingsForEditing;
    private int? activeSession;
    private Status status = new(false, false, false, "Служба ControlTime запускается.");

    public ControlService()
    {
        ServiceName = Name;
        CanHandleSessionChangeEvent = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        cancellation = new CancellationTokenSource();
        worker = Task.Run(() => Run(cancellation.Token));
        listener = Task.Run(() => OpenChannel(cancellation.Token));
    }

    protected override void OnStop()
    {
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        listener?.GetAwaiter().GetResult();
        cancellation?.Dispose();
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        lock (sync)
        {
            if (change.Reason == SessionChangeReason.SessionLock)
                locked.Add(change.SessionId);
            if (change.Reason is SessionChangeReason.SessionUnlock or SessionChangeReason.SessionLogon
                or SessionChangeReason.SessionLogoff)
                locked.Remove(change.SessionId);

            if (activeSession == change.SessionId && (change.Reason is SessionChangeReason.SessionLogon
                or SessionChangeReason.SessionLogoff))
                UpdateSession(null);
        }
    }

    private async Task Run(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                lock (sync)
                {
                    try
                    {
                        Check();
                        lastError = null;
                    }
                    catch (Exception error)
                    {
                        Report(error);
                        int? remaining = configured
                            ? mode.SecondsBeforeShutdown(false, Environment.TickCount64)
                            : null;
                        string message = remaining is int seconds
                            ? $"Не удалось проверить лимит. Выключение через {seconds} с."
                            : "Ошибка проверки ограничений ControlTime.";
                        status = new(configured, mode.IsParent, remaining is not null,
                            message + " " + error.Message, settingsForEditing);
                        if (remaining == 0 && activeSession is int sessionId && !shutdownRequested)
                            Enforce(sessionId);
                    }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task OpenChannel(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = ServiceChannel.Listen();
                    await Listen(pipe, token).ConfigureAwait(false);
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    lock (sync)
                        Report(error);
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task Listen(NamedPipeServerStream pipe, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                int session = ServiceChannel.ClientSession(pipe);
                var request = await ServiceChannel.Read<Request>(pipe, timeout.Token).ConfigureAwait(false);
                Status response;
                lock (sync)
                    response = Respond(session, request);
                await ServiceChannel.Write(pipe, response, timeout.Token).ConfigureAwait(false);

                // Disconnect discards unread bytes. The client closes its end after reading the reply.
                await pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException)
            {
                // A client disconnected, stalled or sent an invalid request.
            }
            catch (Exception error)
            {
                lock (sync)
                    Report(error);
            }
            finally
            {
                pipe.Disconnect();
            }
        }
    }

    private Status Respond(int session, Request request)
    {
        if (session != activeSession && session != Sessions.ActiveConsole)
            return status with { Error = "Откройте ControlTime на экране ноутбука." };

        if (request.Command == "status")
            return status;
        if ((request.Command != "save" && session != activeSession) || locked.Contains(session))
            return status with { Error = "Сеанс Windows ещё не готов. Повторите попытку." };

        if (request.Command == "child")
        {
            if (mode.IsParent)
            {
                mode.UseChildMode();
                return status = status with { ParentMode = false, Message = "Детский режим" };
            }
            return status;
        }

        if (request.Command is not ("parent" or "save"))
            return status with { Error = "Неизвестная команда ControlTime." };

        var profile = Storage.ReadProfile();
        if (profile is null)
            return status with { Error = "Сначала задайте родительский пароль." };

        var now = DateTimeOffset.UtcNow;
        var attempts = Storage.ReadAttempts();
        if (now < attempts.RetryAt)
        {
            int wait = (int)Math.Ceiling((attempts.RetryAt - now).TotalSeconds);
            return status with { Error = $"Повторите попытку через {wait} с." };
        }

        if (!profile.Password.Matches(request.Password))
        {
            Storage.Save(attempts.Failed(now));
            return status with { Error = "Неверный родительский пароль." };
        }

        if (attempts != Attempts.Empty)
            Storage.Save(Attempts.Empty);

        if (request.Command == "parent")
        {
            mode.UseParentMode();
            return status = status with { ParentMode = true, Blocked = false, Message = "Родительский режим" };
        }

        if (request.Settings is not { } settings)
            return status with { Error = "Отсутствует расписание." };
        try
        {
            settings.Validate();
            _ = settings.GetTimeZone();
        }
        catch (Exception error) when (error is InvalidDataException or TimeZoneNotFoundException)
        {
            return status with { Error = error.Message };
        }

        Guid revision = request.ResetToday ? Guid.NewGuid() : profile.Settings.ResetRequest;
        settings = settings with { ResetRequest = revision };
        Storage.Save(profile with { Settings = settings });
        return status = status with { Settings = settings };
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

    private void UpdateSession(int? session)
    {
        if (activeSession == session)
            return;
        activeSession = session;
        mode.UpdateSession(session);
        shutdownRequested = false;
    }

    private void Check()
    {
        var profile = Storage.ReadProfile();
        if (profile is null)
        {
            if (configured)
                throw new InvalidDataException("Профиль ControlTime отсутствует.");

            status = new(false, false, false, "Задайте отдельный пароль родителя.");
            return;
        }

        configured = true;
        var settings = profile.Settings;
        settingsForEditing = settings;

        var sessions = Sessions.List();
        int console = Sessions.ActiveConsole;
        var current = sessions.Find(session => session.Id == console && session.Active);
        UpdateSession(current?.Id);
        bool ready = activeSession is int sessionId && !locked.Contains(sessionId);
        var usage = Storage.ReadUsage();
        var decision = Policy.Evaluate(settings, usage, DateTimeOffset.UtcNow,
            settings.GetTimeZone(), ready && !mode.IsParent);
        if (decision.Usage != usage)
            Storage.Save(decision.Usage);

        int? remaining = mode.SecondsBeforeShutdown(decision.Allowed, Environment.TickCount64);
        status = new(true, mode.IsParent, remaining is not null,
            Describe(settings, decision, remaining), settings);
        if (remaining is null)
            shutdownRequested = false;
        if (remaining != 0 || shutdownRequested || current is null)
            return;

        Enforce(current.Id);
    }

    private void Enforce(int sessionId)
    {
        try
        {
            if (Sessions.List().Any(other => other.Id != sessionId))
                Sessions.LogOff(sessionId);
            else
                Sessions.ShutDown();
            shutdownRequested = true;
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private string Describe(Settings settings, Decision decision, int? remaining)
    {
        if (mode.IsParent)
            return "Родительский режим включён. Детское время не расходуется.";
        if (remaining is int seconds)
            return $"{decision.Reason} Выключение через {seconds} с.";
        if (!decision.Allowed)
            return decision.Reason;

        var usage = decision.Usage;
        int secondsLeft = settings.Sessions[usage.Session].UseMinutes * 60 - usage.UsedSeconds;
        var left = TimeSpan.FromSeconds(secondsLeft);
        return $"Детский режим. Занятие {usage.Session + 1} из {settings.Sessions.Length}. Осталось {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}.";
    }
}
