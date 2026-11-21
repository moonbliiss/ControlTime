using System.Globalization;

namespace ControlTime;

internal sealed class ParentForm : Form
{
    private static readonly string[] Days = ["Воскресенье", "Понедельник", "Вторник", "Среда",
        "Четверг", "Пятница", "Суббота"];

    private readonly List<Scenario> scenarios;
    private Guid activeScenario;
    private Guid savedActiveScenario;
    private readonly ComboBox scenarioPicker = new() { Width = 215, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox timeline = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label activeLabel = Caption("");
    private readonly Label status = Caption("");
    private readonly DataGridView quietHours = new()
    {
        Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window, AllowUserToResizeRows = false,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
    };
    private readonly ComboBox timeZone = new()
    {
        Width = 390, DropDownStyle = ComboBoxStyle.DropDownList,
        DataSource = TimeZoneInfo.GetSystemTimeZones().ToList(),
        DisplayMember = nameof(TimeZoneInfo.DisplayName), ValueMember = nameof(TimeZoneInfo.Id)
    };
    private readonly CheckBox onLimit = new()
    {
        Text = "Выключать ПК при окончании занятия с перерывом, дневного лимита или начале запрета",
        AutoSize = true
    };
    private readonly CheckBox dailyShutdown = new() { Text = "Выключать ПК ежедневно в", AutoSize = true };
    private readonly DateTimePicker shutdownTime = TimeInput(22 * 60);

    public ParentForm(Settings settings)
    {
        scenarios = settings.GetScenarios().ToList();
        savedActiveScenario = activeScenario = settings.GetActiveScenarioId();
        Text = "Настройки ControlTime";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(880, 560);
        MinimumSize = new Size(840, 480);
        Font = new Font("Segoe UI", 10);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateScenariosTab());
        tabs.TabPages.Add(CreateQuietHoursTab());
        tabs.TabPages.Add(CreateShutdownTab());

        var save = new Button { Text = "Сохранить настройки", AutoSize = true };
        save.Click += async (_, _) => await Save(false);
        var reset = new Button { Text = "Сохранить и начать заново сегодня", AutoSize = true };
        reset.Click += async (_, _) => await Save(true);

        var root = CreateLayout();
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs);
        root.Controls.Add(Row(save, reset));
        root.Controls.Add(status);
        Controls.Add(root);

        timeZone.SelectedValue = settings.TimeZoneId;
        onLimit.Checked = settings.ShutDownOnLimit;
        dailyShutdown.Checked = settings.DailyShutdownMinute is not null;
        shutdownTime.Value = DateTime.Today.AddMinutes(settings.DailyShutdownMinute ?? 22 * 60);
        shutdownTime.Enabled = dailyShutdown.Checked;
        foreach (var window in settings.QuietHours)
            AddQuietHours(window.StartDay, window.StartMinute, window.EndMinute);

        ReloadScenarios(scenarios.FindIndex(s => s.Id == activeScenario));
    }

    private TabPage CreateScenariosTab()
    {
        var layout = CreateLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Row(scenarioPicker, Button("Новый", () => NameScenario(true)),
            Button("Переименовать", () => NameScenario(false)), Button("Удалить сценарий", RemoveScenario)));
        layout.Controls.Add(Row(activeLabel, Button("Сделать активным", () =>
        {
            if (SelectedScenario is { } scenario)
                activeScenario = scenario.Id;
            ShowTimeline();
        })));
        layout.Controls.Add(Caption("Составьте порядок занятий и перерывов. Пустой сценарий оставляет только общие запреты."));
        layout.Controls.Add(timeline);
        layout.Controls.Add(Row(Button("Добавить занятие", () => EditStage(true)),
            Button("Изменить", () => EditStage(false)), Button("Удалить", DeleteStage),
            Button("Выше ↑", () => MoveStage(-1)), Button("Ниже ↓", () => MoveStage(1))));

        scenarioPicker.SelectedIndexChanged += (_, _) => ShowTimeline();
        timeline.DoubleClick += (_, _) => EditStage(false);
        return Tab("Сценарии", layout);
    }

    private TabPage CreateQuietHoursTab()
    {
        quietHours.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "День начала", DataSource = Days });
        quietHours.Columns.Add("start", "Начало (ЧЧ:ММ)");
        quietHours.Columns.Add("end", "Конец (ЧЧ:ММ)");

        var layout = CreateLayout();
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Caption("Действуют во всех сценариях. Например, Понедельник 20:00–10:00 означает запрет до утра вторника."));
        layout.Controls.Add(Row(Button("Добавить день", () => AddQuietWindow(false)),
            Button("Добавить на каждый день", () => AddQuietWindow(true)),
            Button("Удалить выбранное", () =>
            {
                if (quietHours.CurrentRow is { } row)
                    quietHours.Rows.Remove(row);
            })));
        layout.Controls.Add(quietHours);
        return Tab("Запретные часы", layout);
    }

    private TabPage CreateShutdownTab()
    {
        var layout = CreateLayout();
        layout.Controls.Add(Row(Caption("Часовой пояс"), timeZone));
        layout.Controls.Add(Row(onLimit));
        layout.Controls.Add(Caption("При выключенной опции ControlTime завершит сеанс Windows, когда детский доступ закроется."));
        layout.Controls.Add(Row(dailyShutdown, shutdownTime));
        layout.Controls.Add(Caption("Ежедневное выключение закрывает детский доступ до полуночи. Время 00:00 закрывает доступ на весь день."));
        layout.Controls.Add(Caption("В родительском режиме эти ограничения не действуют."));
        dailyShutdown.CheckedChanged += (_, _) => shutdownTime.Enabled = dailyShutdown.Checked;
        return Tab("Выключение и время", layout);
    }

    private Scenario? SelectedScenario => scenarioPicker.SelectedItem as Scenario;

    private void ReloadScenarios(int selectedIndex)
    {
        scenarioPicker.Items.Clear();
        scenarioPicker.Items.AddRange(scenarios.Cast<object>().ToArray());
        scenarioPicker.SelectedIndex = Math.Clamp(selectedIndex, 0, scenarios.Count - 1);
        ShowTimeline();
    }

    private void ShowTimeline(int selection = -1)
    {
        activeLabel.Text = "Активный сценарий: " + scenarios.Find(s => s.Id == activeScenario)?.Name;
        timeline.Items.Clear();
        if (SelectedScenario is not { } scenario)
            return;

        for (int i = 0; i < scenario.Sessions.Length; i++)
        {
            var plan = scenario.Sessions[i];
            string breakText = i == scenario.Sessions.Length - 1 ? " · затем конец занятий" :
                plan.BreakMinutes == 0 ? " · дальше без перерыва" : $" · затем перерыв {plan.BreakMinutes} мин.";
            timeline.Items.Add($"{i + 1}. Занятие {plan.UseMinutes} мин.{breakText}");
        }

        if (selection >= 0 && timeline.Items.Count > 0)
            timeline.SelectedIndex = Math.Min(selection, timeline.Items.Count - 1);
    }

    private void NameScenario(bool create)
    {
        if (create && scenarios.Count == Settings.MaxScenarios)
        {
            ShowError($"Можно сохранить до {Settings.MaxScenarios} сценариев.");
            return;
        }
        if (!create && SelectedScenario is null)
            return;

        var input = new TextBox { Width = 310, MaxLength = 48, Text = create ? "" : SelectedScenario!.Name };
        using var dialog = EditDialog(create ? "Новый сценарий" : "Название сценария", Row(input));
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string name = input.Text.Trim();
        int existing = create ? -1 : scenarioPicker.SelectedIndex;
        if (name.Length == 0 || scenarios.Where((_, index) => index != existing)
            .Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("Введите уникальное название сценария.");
            return;
        }
        if (create)
        {
            scenarios.Add(new Scenario(Guid.NewGuid(), name, []));
            ReloadScenarios(scenarios.Count - 1);
        }
        else
        {
            scenarios[existing] = scenarios[existing] with { Name = name };
            ReloadScenarios(existing);
        }
    }

    private void RemoveScenario()
    {
        if (SelectedScenario is not { } scenario)
            return;
        if (scenario.Id == activeScenario)
        {
            ShowError("Сначала выберите другой активный сценарий.");
            return;
        }

        if (MessageBox.Show(this, $"Удалить сценарий «{scenario.Name}»?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        int index = scenarioPicker.SelectedIndex;
        scenarios.RemoveAt(index);
        ReloadScenarios(index);
    }

    private void EditStage(bool add)
    {
        if (SelectedScenario is not { } scenario || (!add && timeline.SelectedIndex < 0))
            return;
        if (add && scenario.Sessions.Length == Settings.MaxSessions)
        {
            ShowError($"Можно добавить до {Settings.MaxSessions} занятий в сценарий.");
            return;
        }

        int index = add ? scenario.Sessions.Length : timeline.SelectedIndex;
        var current = add ? new SessionPlan(60, 0) : scenario.Sessions[index];
        var minutes = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = current.UseMinutes, Width = 80 };
        var pause = new NumericUpDown { Minimum = 0, Maximum = 1440, Value = current.BreakMinutes, Width = 80 };
        using var dialog = EditDialog(add ? "Добавить занятие" : "Изменить занятие",
            Row(Caption("Время за ПК, мин."), minutes), Row(Caption("Перерыв до следующего занятия, мин."), pause));
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var plan = new SessionPlan((int)minutes.Value, (int)pause.Value);
        var stages = scenario.Sessions.ToList();
        if (add)
            stages.Add(plan);
        else
            stages[index] = plan;
        SaveStages(stages, index);
    }

    private void MoveStage(int direction)
    {
        if (SelectedScenario is not { } scenario || timeline.SelectedIndex < 0)
            return;
        int from = timeline.SelectedIndex;
        int to = from + direction;
        if (to < 0 || to >= scenario.Sessions.Length)
            return;

        var stages = scenario.Sessions.ToList();
        (stages[from], stages[to]) = (stages[to], stages[from]);
        SaveStages(stages, to);
    }

    private void DeleteStage()
    {
        if (SelectedScenario is not { } scenario || timeline.SelectedIndex < 0)
            return;
        var stages = scenario.Sessions.ToList();
        int index = timeline.SelectedIndex;
        stages.RemoveAt(index);
        SaveStages(stages, index);
    }

    private void SaveStages(List<SessionPlan> stages, int selection)
    {
        int index = scenarioPicker.SelectedIndex;
        scenarios[index] = scenarios[index] with { Sessions = stages.ToArray() };
        ReloadScenarios(index);
        ShowTimeline(selection);
    }

    private void AddQuietWindow(bool everyDay)
    {
        var start = TimeInput(20 * 60);
        var end = TimeInput(10 * 60);
        var day = new ComboBox { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
        day.Items.AddRange(Days);
        day.SelectedIndex = (int)DayOfWeek.Monday;
        var fields = new List<Control>();
        if (!everyDay)
            fields.Add(Row(Caption("День начала"), day));
        fields.Add(Row(Caption("С"), start, Caption("До"), end));
        fields.Add(Caption("Одинаковое время закрывает доступ на 24 часа."));

        using var dialog = EditDialog(everyDay ? "Запрет на каждый день" : "Новый запрет", fields.ToArray());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        int from = start.Value.Hour * 60 + start.Value.Minute;
        int to = end.Value.Hour * 60 + end.Value.Minute;
        DayOfWeek[] days = everyDay ? Enum.GetValues<DayOfWeek>() : [(DayOfWeek)day.SelectedIndex];
        foreach (var value in days)
            if (!quietHours.Rows.Cast<DataGridViewRow>().Any(row => Cell(row, 0) == Days[(int)value]
                && Cell(row, 1) == FormatTime(from) && Cell(row, 2) == FormatTime(to)))
                AddQuietHours(value, from, to);
    }

    private static DateTimePicker TimeInput(int minutes) => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
        Width = 85, Value = DateTime.Today.AddMinutes(minutes)
    };

    private void AddQuietHours(DayOfWeek day, int start, int end) =>
        quietHours.Rows.Add(Days[(int)day], FormatTime(start), FormatTime(end));

    private async Task Save(bool reset)
    {
        try
        {
            quietHours.EndEdit();
            var windows = quietHours.Rows.Cast<DataGridViewRow>().Select(row => new QuietHours(
                (DayOfWeek)Array.IndexOf(Days, Cell(row, 0)), ReadTime(row, 1), ReadTime(row, 2))).ToArray();
            string zone = (timeZone.SelectedItem as TimeZoneInfo)?.Id
                ?? throw new ArgumentException("Выберите часовой пояс на вкладке «Выключение и время».");
            int? cutoff = dailyShutdown.Checked ? shutdownTime.Value.Hour * 60 + shutdownTime.Value.Minute : null;
            var settings = new Settings([], windows, Guid.Empty, zone, scenarios.ToArray(),
                activeScenario, onLimit.Checked, cutoff).WithActiveSessions();

            if ((reset || activeScenario != savedActiveScenario) && MessageBox.Show(this,
                    "Сохранить настройки и начать активный сценарий с начала на сегодня?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            using var prompt = new PasswordForm("Подтвердите пароль родителя");
            if (prompt.ShowDialog(this) != DialogResult.OK)
                return;

            Enabled = false;
            var response = await ServiceChannel.Send(new Request("save", prompt.TakePassword(), settings, reset));
            status.Text = response.Error ?? "Настройки сохранены.";
            if (response.Error is null)
                savedActiveScenario = activeScenario;
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            Enabled = true;
        }
    }

    private static TableLayoutPanel CreateLayout() => new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(3, 7, 5, 6) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button Button(string title, Action run)
    {
        var button = new Button { Text = title, AutoSize = true };
        button.Click += (_, _) => run();
        return button;
    }

    private static TabPage Tab(string title, Control content)
    {
        var tab = new TabPage(title);
        tab.Controls.Add(content);
        return tab;
    }

    private static Form EditDialog(string title, params Control[] fields)
    {
        var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false
        };
        var layout = new FlowLayoutPanel
        {
            Padding = new Padding(12), FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoSize = true, Dock = DockStyle.Fill
        };
        layout.Controls.AddRange(fields);
        var ok = new Button { Text = "Готово", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(Row(ok, cancel));
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);
        return dialog;
    }

    private static string Cell(DataGridViewRow row, int column) =>
        row.Cells[column].Value?.ToString()?.Trim() ?? "";

    private static int ReadTime(DataGridViewRow row, int column)
    {
        if (TimeOnly.TryParseExact(Cell(row, column), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return time.Hour * 60 + time.Minute;
        throw new FormatException($"Запретные часы, строка {row.Index + 1}. Укажите время в формате ЧЧ:ММ.");
    }

    private static string FormatTime(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private void ShowError(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
