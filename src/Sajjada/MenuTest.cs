using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Sajjada;

/// <summary>
/// SAJJADA_MENU_DRAG_TEST=&lt;folder&gt;: opens the menu and drags it by its title with a pretend
/// pointer (the real mouse is never touched) to every edge and corner of the screen and back,
/// writing where the pointer was and where the menu's window really was on each frame
/// (menu-drag.csv). The pointer stays on the screen, as a real one does.
/// </summary>
sealed partial class RugMenu
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WinRect r);
    [StructLayout(LayoutKind.Sequential)] struct WinRect { public int L, T, R, B; }

    internal async void RunDragTest(string folder)
    {
        Directory.CreateDirectory(folder);
        Open();
        await Frames(40);

        var fake = new Point();
        cursorAt = () => fake;
        var log = new StringBuilder("leg,step,cx,cy,left,top,width,height\n");

        var area = dragArea!;
        var grip = area.PointToScreen(new Point(area.ActualWidth / 2, Math.Min(18, area.ActualHeight / 2)));
        var (whole, _) = Native.ScreenAt(grip);
        Point OnScreen(Point p) => new(Math.Clamp(p.X, whole.Left, whole.Right - 1), Math.Clamp(p.Y, whole.Top, whole.Bottom - 1));

        var far = 4000.0;
        var legs = new (string Name, Vector By)[]
        {
            ("right", new(far, 0)), ("back", new(-far, 0)),
            ("left", new(-far, 0)), ("back", new(far, 0)),
            ("down", new(0, far)), ("back", new(0, -far)),
            ("up", new(0, -far)), ("back", new(0, far)),
            ("downright", new(far, far)), ("back", new(-far, -far)),
            ("upleft", new(-far, -far)), ("back", new(far, far)),
        };

        fake = grip;
        BeginDrag();
        var at = grip;
        foreach (var (name, by) in legs)
        {
            // Steps of 25 pixels a frame: a brisk hand.
            var steps = (int)Math.Ceiling(by.Length / 25);
            var from = at;
            for (var i = 1; i <= steps; i++)
            {
                at = from + by * ((double)i / steps);
                fake = OnScreen(at);
                DragOn();
                await Frames(1);
                GetWindowRect(Handle, out var r);
                log.Append(FormattableString.Invariant($"{name},{i},{fake.X:F0},{fake.Y:F0},{r.L},{r.T},{r.R - r.L},{r.B - r.T}\n"));
            }
            // The real pointer cannot leave the screen, so the next leg starts where it stopped.
            at = fake;
        }
        EndDrag();
        await Frames(10);
        File.WriteAllText(Path.Combine(folder, "menu-drag.csv"), log.ToString());
        File.WriteAllText(Path.Combine(folder, "info.txt"), FormattableString.Invariant(
            $"screen={whole}\nwork={Native.WorkAreaOf(whole)}\ndpi={VisualTreeHelper.GetDpi(card).DpiScaleX}\n"));
        Application.Current.Shutdown();
    }

    /// <summary>
    /// SAJJADA_MENU_OPEN_TEST=&lt;folder&gt;: opens the menu three times at fixed points (the first
    /// open of all, a second at another spot, a third at the same spot) and writes what the menu
    /// was on every frame (menu-open.csv), with the wall clock in UTC ticks so a screen recording
    /// made outside can be laid beside it.
    /// </summary>
    internal async void RunOpenTest(string folder)
    {
        Directory.CreateDirectory(folder);
        var log = new StringBuilder("utc_ticks,event,opacity,scale,visible,left,top,width,height\n");
        void Note(string what)
        {
            GetWindowRect(hwnd, out var r);
            log.Append(FormattableString.Invariant(
                $"{DateTime.UtcNow.Ticks},{what},{card.Opacity:F3},{cardScale.ScaleX:F3},{(Native.IsVisible(hwnd) ? 1 : 0)},{r.L},{r.T},{r.R - r.L},{r.B - r.T}\n"));
        }
        var seen = TimeSpan.MinValue;
        void OnFrame(object? s, EventArgs e)
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == seen) return;
            seen = t;
            Note("frame");
        }

        await Task.Delay(2500);
        CompositionTarget.Rendering += OnFrame;
        var spots = new[] { new Point(700, 200), new Point(900, 250), new Point(900, 250) };
        for (var i = 0; i < spots.Length; i++)
        {
            var spot = spots[i];
            cursorAt = () => spot;
            Note($"open{i + 1}");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Open();
            Note(FormattableString.Invariant($"opened{i + 1} in {clock.Elapsed.TotalMilliseconds:F1} ms"));
            await Task.Delay(1500);
            Note($"close{i + 1}");
            Close();
            await Task.Delay(1200);
        }
        CompositionTarget.Rendering -= OnFrame;
        File.WriteAllText(Path.Combine(folder, "menu-open.csv"), log.ToString());
        Application.Current.Shutdown();
    }

    static TimeSpan lastFrame = TimeSpan.MinValue;

    static async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var done = new TaskCompletionSource();
            EventHandler? handler = null;
            handler = (_, e) =>
            {
                var t = ((RenderingEventArgs)e).RenderingTime;
                if (t == lastFrame) return;
                lastFrame = t;
                CompositionTarget.Rendering -= handler;
                done.TrySetResult();
            };
            CompositionTarget.Rendering += handler;
            await done.Task;
        }
    }
}
