namespace ControlTime;

internal sealed class PasswordForm : Form
{
    private readonly TextBox password = new() { UseSystemPasswordChar = true, MaxLength = 128, Width = 350 };
    private readonly TextBox confirmation = new() { UseSystemPasswordChar = true, MaxLength = 128, Width = 350 };

    public PasswordForm(string title, bool setup = false)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10);
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(20), Dock = DockStyle.Fill
        };
        layout.Controls.Add(new Label { Text = setup ? "Новый пароль ControlTime (от 12 символов)" : "Пароль ControlTime", AutoSize = true });
        layout.Controls.Add(password);
        if (setup)
        {
            layout.Controls.Add(new Label { Text = "Повторите пароль", AutoSize = true });
            layout.Controls.Add(confirmation);
        }

        var ok = new Button { Text = "Подтвердить", AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (setup && (!ParentPassword.ValidLength(password.Text) || password.Text != confirmation.Text))
            {
                MessageBox.Show(this, "Введите одинаковый пароль длиной от 12 до 128 символов.", Text);
                return;
            }
            confirmation.Clear();
            DialogResult = DialogResult.OK;
        };
        layout.Controls.Add(ok);
        Controls.Add(layout);
        AcceptButton = ok;
        Shown += (_, _) => password.Focus();
    }

    public string TakePassword()
    {
        string value = password.Text;
        password.Clear();
        return value;
    }
}
