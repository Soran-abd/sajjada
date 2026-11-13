using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Sajjada;

/// <summary>
/// SAJJADA_MOTION_TEST=&lt;folder&gt;: replays every kind of movement through the same code the
/// mouse drives (with a pretend pointer, the real mouse is never touched) and records:
///   frames.csv  every rendered frame: scene, gap since the frame before, time spent drawing;
///   window.csv  every position and size the window took, seen by a separate thread that looks
///               as often as it can, so even a state that lasts a fraction of a frame shows up.
/// Then the program closes. Settings are changed by the test; keep a copy to put back.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WinRect r);
    [StructLayout(LayoutKind.Sequential)] struct WinRect { public int L, T, R, B; }

    volatile string scene = "start";
    double lastDrawMs;

    async void RunMotionTest(string folder)
    {
        Directory.CreateDirectory(folder);
        // WPF may raise its rendering event more than once for the same frame; a frame is
        // counted once, by its own rendering time. "gap" is the time since the frame before,
        // on the wall clock, so a stalled drawing thread shows as a long gap.
        // "moving": the rug's animation ran this frame, so the picture changed (a late frame
        // there is a visible hitch); otherwise nothing on screen changed and a late frame is unseen.
        var frames = new StringBuilder("scene,t_ms,gap_ms,draw_ms,mode,gc0,gc1,gc2,moving\n");
        var clock = Stopwatch.StartNew();
        double last = 0;
        var lastFrameTime = TimeSpan.MinValue;
        var wasRendering = false;
        void OnRender(object? s, EventArgs e)
        {
            var frameTime = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
            if (frameTime == lastFrameTime) return;
            lastFrameTime = frameTime;
            var t = clock.Elapsed.TotalMilliseconds;
            frames.Append(FormattableString.Invariant($"{scene},{t:F2},{t - last:F2},{lastDrawMs:F2},{mode},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{(rendering || wasRendering ? 1 : 0)}\n"));
            wasRendering = rendering;
            last = t;
        }
        System.Windows.Media.CompositionTarget.Rendering += OnRender;

        // Window watcher on its own thread.
        var hwnd = Hwnd;
        var windows = new StringBuilder("scene,t_ms,left,top,width,height\n");
        // The watcher keeps one processor busy; "nowatch" leaves it off when only frame pacing is measured.
        var watching = !Knob("nowatch");
        var watcher = new Thread(() =>
        {
            string? prev = null;
            while (watching)
            {
                GetWindowRect(hwnd, out var r);
                var now = $"{r.L},{r.T},{r.R - r.L},{r.B - r.T}";
                if (now != prev)
                {
                    lock (windows) windows.Append(FormattableString.Invariant($"{scene},{clock.Elapsed.TotalMilliseconds:F2},{now}\n"));
                    prev = now;
                }
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        watcher.Start();

        var fake = new Point();
        cursorAt = () => fake;
        var dpi = VisualTreeHelper.GetDpi(this);
        Point Screen(Point inWindow) => PointToScreen(inWindow);
        // Moves the pretend pointer to a point of the window, in steps over some frames.
        async Task Glide(Point from, Point to, int frameCount, bool drive = true)
        {
            for (var i = 1; i <= frameCount; i++)
            {
                var p = from + (to - from) * ((double)i / frameCount);
                fake = Screen(p);
                if (drive) Move(PointFromScreen(fake));
                await NextFrame();
            }
        }
        async Task Wait(int frameCount) { for (var i = 0; i < frameCount; i++) await NextFrame(); }

        await Wait(30);
        var c = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);

        // "hoveronly": many short corner lifts, where a movement starts most often.
        if (Knob("hoveronly"))
        {
            for (var round = 0; round < 12; round++)
                foreach (var (corner, i) in Corners.Select((x, i) => (x, i)))
                {
                    scene = $"hover{i}";
                    var inside = corner + Inward(corner) * 14;
                    await Glide(c, inside, 12);
                    await Wait(35);
                    await Glide(inside, c, 12);
                    await Wait(40);
                }
            scene = "end";
            goto Finish;
        }

        // "foldonly": three rounds of folding by hand, nothing else (a long, steady sample of fold frames).
        if (Knob("foldonly"))
        {
            for (var round = 0; round < 3; round++)
                foreach (var (corner, i) in Corners.Select((x, i) => (x, i)))
                {
                    scene = $"peel{i}";
                    var inward = Inward(corner);
                    var grab = corner + inward * 6;
                    fake = Screen(grab); Down(grab);
                    var far = corner + new Vector(inward.X * body.Width * 0.6, inward.Y * body.Height * 0.25);
                    await Glide(grab, far, 45);
                    Up();
                    await Wait(30);
                    var tip = pose.PeelTip;
                    fake = Screen(tip); Down(tip);
                    await Glide(tip, corner, 35);
                    Up();
                    await Wait(50);
                }
            scene = "end";
            goto Finish;
        }

        // 1. Resting on each corner: it lifts a little, then lies down again.
        foreach (var (corner, i) in Corners.Select((x, i) => (x, i)))
        {
            scene = $"hover{i}";
            var inside = corner + Inward(corner) * 14;
            await Glide(c, inside, 12);
            await Wait(35);
            await Glide(inside, c, 12);
            await Wait(40);
        }

        // 2. Folding each corner by hand far over the rug, letting go (it stays up), then
        //    picking it up again and laying it back.
        foreach (var (corner, i) in Corners.Select((x, i) => (x, i)))
        {
            scene = $"peel{i}";
            var inward = Inward(corner);
            var grab = corner + inward * 6;
            fake = Screen(grab); Down(grab);
            var far = corner + new Vector(inward.X * body.Width * 0.6, inward.Y * body.Height * 0.25);
            await Glide(grab, far, 45);
            Up();
            await Wait(30);
            var tip = pose.PeelTip;
            fake = Screen(tip); Down(tip);
            await Glide(tip, corner, 35);
            Up();
            await Wait(50);
        }

        // 3. Carrying the rug: a slow circle, then a quick throw, then letting go.
        scene = "carry";
        // All in screen pixels: the window moves under the pointer while carried.
        var pressed = Screen(c);
        fake = pressed; Down(c);
        for (var k = 1; k <= 3; k++)
        {
            fake = pressed + new Vector(4 * k * dpi.DpiScaleX, 0);
            Move(PointFromScreen(fake));
            await NextFrame();
        }
        var start = fake;
        for (var k = 1; k <= 90; k++)
        {
            var a = k / 90.0 * 2 * Math.PI;
            fake = start + new Vector(Math.Sin(a) * 200, (1 - Math.Cos(a)) * 120) * dpi.DpiScaleX;
            Move(PointFromScreen(fake));
            await NextFrame();
        }
        var fastFrom = fake;
        for (var k = 1; k <= 8; k++)
        {
            fake = fastFrom + new Vector(-60 * k, 25 * k);
            Move(PointFromScreen(fake));
            await NextFrame();
        }
        for (var k = 1; k <= 8; k++)
        {
            fake = fastFrom + new Vector(-60 * (8 - k), 25 * (8 - k));
            Move(PointFromScreen(fake));
            await NextFrame();
        }
        Up();
        await Wait(60);

        // 4. Resizing from each edge: smaller, then back larger.
        foreach (var edge in new[] { "right", "bottom", "left", "top" })
        {
            scene = $"resize-{edge}";
            Point grip = edge switch
            {
                "right" => new Point(body.Right - 3, c.Y),
                "left" => new Point(body.Left + 3, c.Y),
                "bottom" => new Point(c.X, body.Bottom - 3),
                _ => new Point(c.X, body.Top + 3),
            };
            var inwardShift = edge switch
            {
                "right" => new Vector(-1, 0), "left" => new Vector(1, 0), "bottom" => new Vector(0, -1), _ => new Vector(0, 1),
            };
            var screenGrip = Screen(grip);
            fake = screenGrip; Down(PointFromScreen(fake));
            for (var k = 1; k <= 30; k++)
            {
                fake = screenGrip + inwardShift * (k * 8 * dpi.DpiScaleX);
                Move(PointFromScreen(fake));
                await NextFrame();
            }
            for (var k = 29; k >= 0; k--)
            {
                fake = screenGrip + inwardShift * (k * 8 * dpi.DpiScaleX);
                Move(PointFromScreen(fake));
                await NextFrame();
            }
            Up();
            await Wait(30);
            c = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);
        }

        // 5. A desktop icon dragged over the rug and away again.
        scene = "dragover";
        var desktopFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "x.txt");
        for (var k = 0; k <= 40; k++)
        {
            var p = new Point(body.Left + body.Width * k / 40.0, c.Y + 40 * Math.Sin(k / 6.0));
            IconsOver(new[] { desktopFile }, p);
            await NextFrame();
        }
        IconsLeft();
        await Wait(50);

        // 6. Changing the design several times.
        scene = "pattern";
        foreach (var id in new[] { "marble", "ishtar", "cognac", "kazak", "arabesque", "almasdar", "pebbles" })
        {
            Lay(Patterns.Find(id));
            await Wait(20);
        }

        scene = "end";
    Finish:
        await Wait(10);
        watching = false;
        watcher.Join();
        System.Windows.Media.CompositionTarget.Rendering -= OnRender;
        File.WriteAllText(Path.Combine(folder, "frames.csv"), frames.ToString());
        lock (windows) File.WriteAllText(Path.Combine(folder, "window.csv"), windows.ToString());
        File.WriteAllText(Path.Combine(folder, "info.txt"), FormattableString.Invariant(
            $"dpi={dpi.DpiScaleX}\nscreen={screen}\nrug={body.Width}x{body.Height}\n"));
        Application.Current.Shutdown();
    }

    // The next new frame (one with a rendering time not seen before).
    static TimeSpan lastTestFrame = TimeSpan.MinValue;

    static Task NextFrame()
    {
        var done = new TaskCompletionSource();
        EventHandler? handler = null;
        handler = (_, e) =>
        {
            var frameTime = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
            if (frameTime == lastTestFrame) return;
            lastTestFrame = frameTime;
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            done.TrySetResult();
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
        return done.Task;
    }
}
