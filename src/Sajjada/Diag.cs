using System.IO;
using System.Runtime.InteropServices;

namespace Sajjada;

/// <summary>SAJJADA_MENU_LOG=&lt;file&gt;: writes what the menu goes through (for faults that only a real hand shows).</summary>
static class Diag
{
    static readonly string? file = Environment.GetEnvironmentVariable("SAJJADA_MENU_LOG");
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    public static bool On => file != null;

    public static void Note(string what)
    {
        if (file == null) return;
        File.AppendAllText(file, FormattableString.Invariant($"{clock.Elapsed.TotalMilliseconds,9:F1}  fg={GetForegroundWindow()}  {what}\n"));
    }
}
