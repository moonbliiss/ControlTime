using System.Security.Principal;
using System.ServiceProcess;

namespace ControlTime;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--service"])
        {
            ServiceBase.Run(new ControlService());
            return;
        }

        ApplicationConfiguration.Initialize();
        if (args is ["--setup"])
        {
            SetUp();
            return;
        }

        using var instance = new Mutex(true, @"Local\ControlTimeDashboard", out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ControlTimeShow");
        if (first)
            Application.Run(new Dashboard(args is ["--tray"], show));
        else if (args is not ["--tray"])
            show.Set();
    }

    private static void SetUp()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Запустите первоначальную настройку от имени администратора Windows.", "ControlTime");
            return;
        }

        try
        {
            var existing = Storage.ReadProfile();
            if (existing is not null && MessageBox.Show(
                    "Заменить текущий пароль ControlTime? Расписание и оставшееся время сохранятся.",
                    "ControlTime", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            using var form = new PasswordForm("Настройка ControlTime", setup: true);
            if (form.ShowDialog() != DialogResult.OK)
                return;

            var settings = existing?.Settings ?? Storage.ReadLegacySettings() ??
                new Settings([new(60, 180), new(60, 0)], [], Guid.Empty, TimeZoneInfo.Local.Id);
            Storage.InitializePrivateDirectory();
            Storage.Save(new Profile(settings, ParentPassword.Create(form.TakePassword())));
            Storage.Save(Attempts.Empty);
            MessageBox.Show("Пароль сохранён. Откройте ControlTime из меню «Пуск», чтобы изменить расписание.",
                "ControlTime");
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "ControlTime", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
