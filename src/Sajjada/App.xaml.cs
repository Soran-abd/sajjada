using System.Windows;

namespace Sajjada;

public partial class App : Application
{
    // One rug at a time.
    static Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        single = new Mutex(true, "Sajjada.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
