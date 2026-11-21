using System.Diagnostics;

namespace ControlTime;

internal sealed class Dashboard : Form
{
    private readonly Label message = new() { AutoSize = true, Padding = new Padding(2, 4, 2, 12) };
    private readonly Label feedback = new() { AutoSize = true, ForeColor = Color.DarkRed };
    private readonly TextBox password = new() { UseSystemPasswordChar = true, MaxLength = 128, Width = 325 };
    private readonly Button unlock = new() { Text = "Включить родительский режим", AutoSize = true };
    private readonly Button endParent = new() { Text = "Вернуться в детский режим", AutoSize = true };
    private readonly Button preferences = new() { Text = "Расписание", AutoSize = true };
    private readonly Button initialize = new() { Text = "Задать пароль родителя", AutoSize = true };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    private readonly NotifyIcon tray;
    private Status? status;
    private bool pending;
    private bool blocked;
    private Form? settingsWindow;

    public Dashboard(bool startHidden, EventWaitHandle showWindow)
    {
        Text = "ControlTime";
        Font = new Font("Segoe UI", 12);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(630, 300);
        MinimumSize = new Size(600, 295);

        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown,
            Anchor = AnchorStyles.None, Padding = new Padding(12)
        };
        actions.Controls.AddRange([message, password, unlock, endParent, preferences, initialize, feedback]);
        center.Controls.Add(actions, 1, 1);
        Controls.Add(center);
        AcceptButton = unlock;
        unlock.Click += async (_, _) =>
        {
            string value = password.Text;
            password.Clear();
            await Send(new Request("parent", value));
        };
        endParent.Click += async (_, _) => await Send(new Request("child"));
        preferences.Click += (_, _) =>
        {
            if (status?.Settings is not { } settings)
                return;

            if (settingsWindow is { IsDisposed: false })
            {
                settingsWindow.Activate();
                return;
            }

            settingsWindow = new ParentForm(settings);
            settingsWindow.Show(this);
        };
        initialize.Click += (_, _) =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--setup")
                {
                    UseShellExecute = true, Verb = "runas"
                });
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                feedback.Text = error.Message;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть ControlTime", null, (_, _) => Reveal());
        tray = new NotifyIcon { Text = "ControlTime", Icon = SystemIcons.Shield, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Reveal();

        Apply(new Status(false, false, false, "Подключение к службе ControlTime…"));
        initialize.Visible = false;
        Shown += async (_, _) =>
        {
            if (startHidden)
                Hide();
            refresh.Start();
            await Poll();
        };
        refresh.Tick += async (_, _) =>
        {
            if (showWindow.WaitOne(0))
                Reveal();
            await Poll();
        };
        FormClosing += (_, args) =>
        {
            if (args.CloseReason != CloseReason.UserClosing)
                return;
            args.Cancel = true;
            if (!blocked)
                Hide();
        };
        FormClosed += (_, _) =>
        {
            refresh.Dispose();
            tray.Dispose();
            menu.Dispose();
        };
    }

    private async Task Poll()
    {
        if (pending)
            return;

        pending = true;
        try
        {
            Apply(await ServiceChannel.Send(new Request("status")));
        }
        catch (Exception)
        {
            message.Text = "Служба ControlTime недоступна. Проверяем подключение…";
            initialize.Visible = false;
        }
        finally
        {
            pending = false;
        }
    }

    private async Task Send(Request request)
    {
        if (status?.Configured != true)
            return;

        try
        {
            unlock.Enabled = false;
            endParent.Enabled = false;
            var response = await ServiceChannel.Send(request);
            Apply(response);
            feedback.Text = response.Error ?? "";
        }
        catch (Exception)
        {
            feedback.Text = "Служба ControlTime не ответила. Повторите попытку.";
        }
        finally
        {
            unlock.Enabled = true;
            endParent.Enabled = true;
        }
    }

    private void Apply(Status value)
    {
        status = value;
        message.Text = value.Error ?? value.Message;
        password.Visible = value.Configured && !value.ParentMode;
        unlock.Visible = value.Configured && !value.ParentMode;
        endParent.Visible = value.Configured && value.ParentMode;
        preferences.Visible = value.Configured && !value.Blocked && value.Settings is not null;
        initialize.Visible = !value.Configured;
        if (blocked == value.Blocked)
            return;

        blocked = value.Blocked;
        if (blocked)
        {
            settingsWindow?.Close();
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            WindowState = FormWindowState.Maximized;
            Reveal();
            password.Focus();
        }
        else
        {
            TopMost = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.Sizable;
        }
    }

    private void Reveal()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }
}
