using System.IO;
using System.Runtime.InteropServices;

namespace Sajjada;

/// <summary>
/// "Start with Windows": a shortcut to this program in the user's Startup folder. It is the same
/// shortcut the installer makes when its box is ticked, so the switch in the menu shows the
/// installer's choice, and the uninstaller removes it whichever of the two made it.
/// </summary>
static class AutoStart
{
    static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Text.AppName + ".lnk");

    public static bool IsOn => File.Exists(LinkPath);

    public static void Set(bool on)
    {
        try
        {
            if (!on)
            {
                // Only this program's own shortcut, by its exact name.
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                return;
            }
            if (Environment.ProcessPath is not { } exe || Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(LinkPath);
                link.TargetPath = exe;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.Description = "Sajjada";
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { }
    }
}
