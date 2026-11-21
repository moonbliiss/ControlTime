using System.Globalization;

namespace ControlTime;

internal sealed class ParentForm : Form
{
    private readonly ComboBox timeZone = new()
    {
        Width = 330, DropDownStyle = ComboBoxStyle.DropDownList,
        DataSource = TimeZoneInfo.GetSystemTimeZones().ToList(),
        DisplayMember = nameof(TimeZoneInfo.DisplayName),
        ValueMember = nameof(TimeZoneInfo.Id)
    };
    private readonly Label status = new() { AutoSize = true, Padding = new Padding(9, 7, 0, 0) };
    private readonly DataGridView sessions = CreateGrid();
    private readonly DataGridView quietHours = CreateGrid();
    private readonly string[] days = ["Воскресенье", "Понедельник", "Вторник", "Среда",
        "Четверг", "Пятница", "Суббота"];

    public ParentForm(Settings settings)
    {
        Text = "ControlTime — родительские настройки";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 590);
        MinimumSize = new Size(670, 510);
        Font = new Font("Segoe UI", 10);

        sessions.Columns.Add("duration", "Занятие, мин.");
        sessions.Columns.Add("break", "Перерыв после занятия, мин.");
        quietHours.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "День начала",
            DataSource = days
        });
        quietHours.Columns.Add("start", "Начало (ЧЧ:ММ)");
        quietHours.Columns.Add("end", "Конец (ЧЧ:ММ)");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var scheduleZone = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        scheduleZone.Controls.Add(new Label { Text = "Часовой пояс расписания", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        scheduleZone.Controls.Add(timeZone);
        layout.Controls.Add(scheduleZone, 0, 0);
        layout.Controls.Add(Caption("Занятия по порядку. После последнего занятия доступ закрывается до нового дня."), 0, 1);
        layout.Controls.Add(sessions, 0, 2);
        layout.Controls.Add(Caption("Запрет по дням. Одинаковое время начала и конца задаёт интервал в 24 часа."), 0, 3);
        layout.Controls.Add(quietHours, 0, 4);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var reset = new Button { Text = "Сбросить время на сегодня", AutoSize = true };
        save.Click += async (_, _) => await Save(false);
        reset.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Начать расписание занятий заново и сохранить настройки?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await Save(true);
        };
        actions.Controls.Add(save);
        actions.Controls.Add(reset);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(status, 0, 6);

        timeZone.SelectedValue = settings.TimeZoneId;
        foreach (var entry in settings.Sessions)
            sessions.Rows.Add(entry.UseMinutes, entry.BreakMinutes);
        foreach (var entry in settings.QuietHours)
            quietHours.Rows.Add(days[(int)entry.StartDay], FormatTime(entry.StartMinute), FormatTime(entry.EndMinute));
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(3, 9, 0, 5)
    };

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.RowHeaderSelect
    };

    private async Task Save(bool reset)
    {
        try
        {
            sessions.EndEdit();
            quietHours.EndEdit();
            var plans = Rows(sessions).Select(row => new SessionPlan(
                ReadNumber(row, 0), ReadNumber(row, 1, allowEmpty: true))).ToArray();
            var windows = Rows(quietHours).Select(row => new QuietHours(
                (DayOfWeek)Array.IndexOf(days, Cell(row, 0)), ReadTime(row, 1), ReadTime(row, 2))).ToArray();
            string zone = (timeZone.SelectedItem as TimeZoneInfo)?.Id
                ?? throw new ArgumentException("Выберите часовой пояс расписания.");
            var settings = new Settings(plans, windows, Guid.Empty, zone);
            settings.Validate();
            using var prompt = new PasswordForm("Подтвердите пароль родителя");
            if (prompt.ShowDialog(this) != DialogResult.OK)
                return;

            Enabled = false;
            var response = await ServiceChannel.Send(new Request("save", prompt.TakePassword(), settings, reset));
            status.Text = response.Error ?? "Настройки сохранены.";
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            Enabled = true;
        }
    }

    private static IEnumerable<DataGridViewRow> Rows(DataGridView grid) =>
        grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow);

    private static string Cell(DataGridViewRow row, int column) =>
        row.Cells[column].Value?.ToString()?.Trim() ?? "";

    private static int ReadNumber(DataGridViewRow row, int column, bool allowEmpty = false)
    {
        string value = Cell(row, column);
        if (allowEmpty && value.Length == 0)
            return 0;
        if (int.TryParse(value, out int number))
            return number;
        throw new FormatException($"Строка {row.Index + 1}. Укажите длительность в минутах.");
    }

    private static int ReadTime(DataGridViewRow row, int column)
    {
        if (TimeOnly.TryParseExact(Cell(row, column), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return time.Hour * 60 + time.Minute;
        throw new FormatException($"Строка {row.Index + 1}. Укажите время в формате ЧЧ:ММ.");
    }

    private static string FormatTime(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private void ShowError(Exception error) =>
        MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
