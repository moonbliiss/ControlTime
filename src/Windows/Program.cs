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
        Application.Run(new ParentForm());
    }
}
