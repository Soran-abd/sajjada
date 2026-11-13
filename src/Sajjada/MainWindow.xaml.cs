using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Sajjada;

public partial class MainWindow : Window
{
    // The window covers the whole screen the rug is on, and never moves or changes size while
    // the rug is handled. Moving or resizing a transparent window makes Windows drop frames
    // (measured on a bare window: 30-42% late frames, against 0.2% for one that stays put), so
    // the rug is carried, folded and resized inside the window instead. Everywhere but the rug
    // the window is fully transparent: clicks and dragged icons pass straight to the desktop.
    Rect screen;          // the screen the window covers, physical pixels
    DpiScale screenDpi = new(1, 1);

    // Saved positions are the spot of a window with 48 px of room around the rug, as earlier
    // versions kept them, so settings from any version put the rug back where it was.
    const double SavedGutter = 48;

    // Diagnostic switches for the motion test (SAJJADA_KNOBS=a,b,...).
    static bool Knob(string name) =>
        (Environment.GetEnvironmentVariable("SAJJADA_KNOBS") ?? "").Split(',').Contains(name);
    const double RugMinWidth = 240, RugMaxWidth = 960, Aspect = 1.5;
    // Thin band along the rug's edge that resizes; the rest moves or folds.
    const double EdgeIn = 7, EdgeOut = 6;

    enum Mode { Idle, Pressing, Carrying, Peeling, Resizing }
    enum Edge { None, Left, Top, Right, Bottom }

    readonly Storage storage = new();
    readonly RugGeometry geometry = new();
    // A corner lifts only when the cursor rests on it, not when it merely passes over.
    readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    Rect body;
    RugPose pose;
    Point tipTarget;
    Vector tipVelocity;
    bool peelOpen;
    Point? hoverCorner;

    Mode mode;
    Point pressedAt;
    Vector grabOffset;
    (Point Cursor, Point Window, double Width) dragFrom;
    Edge resizeEdge;

    Point lastRugSpot;
    Vector velocity, swayVelocity;
    double carryLift;
    TimeSpan lastFrame;
    bool rendering;

    public MainWindow()
    {
        InitializeComponent();
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (hoverCorner is { } c && mode == Mode.Idle && !peelOpen) Peek(c, 12);
        };

        // Older or hand-edited settings may name a pattern that no longer exists.
        storage.State.Pattern = Patterns.Find(storage.State.Pattern).Id;
        // The very first time (never placed yet) the rug is laid out large: as wide as fits
        // comfortably on the main screen, up to the largest size.
        if (storage.State.ScreenX is null && double.IsNaN(storage.State.Left)) storage.State.Width = FirstWidth();
        storage.State.Width = Math.Clamp(storage.State.Width, RugMinWidth, RugMaxWidth);

        BuildScenes();
        ApplySize(storage.State.Width);
        ApplyPattern(Patterns.Find(storage.State.Pattern));
        BuildMenu();

        // Owned by the desktop so "show desktop" (Win+D) does not hide the rug.
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (!Knob("front")) new WindowInteropHelper(this).Owner = Native.GetShellWindow();
        SourceInitialized += (_, _) =>
        {
            var hwnd = Hwnd;
            // Before the window is first shown: cover the rug's screen and lay the rug where it was left.
            PlaceAtStart();
            // "front" (motion test only): in front of other windows, to compare with being behind them.
            if (Knob("front")) { Topmost = true; return; }
            HwndSource.FromHwnd(hwnd).AddHook(Native.StayAtBottom);
            ListenForIcons(hwnd);
        };
        // Screens changed (resolution, scaling, one plugged in or out): cover the rug's screen again.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnScreensChanged;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => OnScreensChanged(this, EventArgs.Empty));
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnScreensChanged;

        Redraw();
    }

    // ---------- the window and its screen ----------

    /// <summary>
    /// The rug where it was left, if that spot is still on a connected screen (a second screen
    /// may have been unplugged since); otherwise in the middle of the main screen.
    /// </summary>
    void PlaceAtStart()
    {
        var s = storage.State;
        // The spot is kept as a window with 48 px of room around the rug: the rug starts 48 px in.
        Point? savedWindow = s.ScreenX is { } sx && s.ScreenY is { } sy ? new Point(sx, sy) : null;
        if (savedWindow is { } w && !Native.OnAScreen((int)w.X + 88, (int)w.Y + 88)) savedWindow = null;

        var probe = savedWindow is { } at ? at + new Vector(88, 88) : new Point(0, 0);
        var (whole, work) = Native.ScreenAt(probe);
        CoverScreen(whole);
        var dpi = screenDpi;

        Point rugOnScreen;
        if (savedWindow is { } saved)
            rugOnScreen = saved + new Vector(SavedGutter * dpi.DpiScaleX, SavedGutter * dpi.DpiScaleY);
        else if (!double.IsNaN(s.Left) && FitsOnScreen(s.Left, s.Top))
            rugOnScreen = new Point((s.Left + SavedGutter) * dpi.DpiScaleX, (s.Top + SavedGutter) * dpi.DpiScaleY);  // oldest settings, in scaled units
        else
            rugOnScreen = new Point(work.X + (work.Width - body.Width * dpi.DpiScaleX) / 2,
                                    work.Y + (work.Height - body.Height * dpi.DpiScaleY) / 2);

        SetRugSize(body.Width, KeepOnScreen(ToWindow(rugOnScreen), body.Size));
        RenderShadows();
        Redraw();
    }

    /// <summary>Makes the window cover a whole screen, given in physical pixels.</summary>
    void CoverScreen(Rect whole)
    {
        screen = whole;
        // Twice: moving onto a screen with other scaling makes WPF resize the window for it
        // first; the second call, now on that screen, sets the exact size.
        for (var i = 0; i < 2; i++)
            Native.SetWindowBounds(Hwnd, (int)whole.X, (int)whole.Y, (int)whole.Width, (int)whole.Height);
        screenDpi = VisualTreeHelper.GetDpi(this);
        (Width, Height) = (whole.Width / screenDpi.DpiScaleX, whole.Height / screenDpi.DpiScaleY);
        LayOutRug();
    }

    // How far past the rug's edge anything is ever drawn: the farthest a folded corner reaches
    // (a corner pulled nearly flat along the rug swings its neighbour out by about 0.11 of the
    // rug's height, 72 px on the largest rug), plus fringe and the soft shadows.
    const double Reach = 130;

    /// <summary>
    /// The two 3D views cover just the rug and the room a fold can reach, not the whole screen:
    /// drawing 3D over the whole screen every frame cost visible frames. Where they are is plain
    /// layout inside the window, so they follow the rug at no cost. One world unit stays one
    /// pixel of the window, so the rug's coordinates do not depend on where the views are.
    /// </summary>
    void LayOutViews()
    {
        var area = Rect.Intersect(Rect.Inflate(body, Reach, Reach), new Rect(0, 0, Width, Height));
        if (area.IsEmpty) area = new Rect(0, 0, 1, 1);
        foreach (var view in new[] { BaseView, FlapView })
        {
            view.HorizontalAlignment = HorizontalAlignment.Left;
            view.VerticalAlignment = VerticalAlignment.Top;
            view.Margin = new Thickness(area.X, area.Y, 0, 0);
            (view.Width, view.Height) = (area.Width, area.Height);
            view.Camera = new OrthographicCamera
            {
                // Looking along +z with y down, centred on the area the view covers.
                Position = new Point3D(area.X + area.Width / 2, area.Y + area.Height / 2, -1000), LookDirection = new Vector3D(0, 0, 1),
                UpDirection = new Vector3D(0, -1, 0), Width = area.Width, NearPlaneDistance = 1, FarPlaneDistance = 3000,
            };
        }
    }

    /// <summary>A point on the screen, in physical pixels, in the window's own units.</summary>
    Point ToWindow(Point onScreen) =>
        new((onScreen.X - screen.X) / screenDpi.DpiScaleX, (onScreen.Y - screen.Y) / screenDpi.DpiScaleY);

    /// <summary>A top-left for a rug of that size that keeps all of it on the window's screen.</summary>
    Point KeepOnScreen(Point topLeft, Size size) => new(
        Math.Clamp(topLeft.X, 0, Math.Max(0, Width - size.Width)),
        Math.Clamp(topLeft.Y, 0, Math.Max(0, Height - size.Height)));

    void OnScreensChanged(object? sender, EventArgs e)
    {
        if (Hwnd == IntPtr.Zero) return;
        // Where the rug was, in physical pixels, worked out from the screen the window covered.
        var rugOnScreen = new Point(screen.X + body.X * screenDpi.DpiScaleX, screen.Y + body.Y * screenDpi.DpiScaleY);
        var center = rugOnScreen + new Vector(body.Width * screenDpi.DpiScaleX / 2, body.Height * screenDpi.DpiScaleY / 2);
        CoverScreen(Native.ScreenAt(center).Whole);
        MoveRug(KeepOnScreen(ToWindow(rugOnScreen), body.Size));
        RenderShadows();
        Redraw();
    }

    // ---------- scene ----------

    readonly GeometryModel3D baseRug = new(), flapRug = new(), baseFringe = new(), flapFringe = new();

    void BuildScenes()
    {
        baseRug.Geometry = geometry.BaseMesh;
        flapRug.Geometry = geometry.FlapMesh;
        baseFringe.Geometry = geometry.BaseFringe;
        flapFringe.Geometry = geometry.FlapFringe;
        BaseView.Children.Add(Scene(baseRug, baseFringe));
        FlapView.Children.Add(Scene(flapRug, flapFringe));
    }

    static ModelVisual3D Scene(GeometryModel3D rug, GeometryModel3D fringe) => new()
    {
        Content = new Model3DGroup
        {
            Children =
            {
                new AmbientLight(Color.FromRgb(0x73, 0x73, 0x73)),
                new DirectionalLight(Color.FromRgb(0xF0, 0xF0, 0xF0), RugGeometry.LightDir),
                rug,
                fringe,
            }
        }
    };

    void ApplyPattern(Pattern pattern)
    {
        var texture = Patterns.Texture(pattern);
        ApplyPattern(pattern, texture, pattern.IsLeather ? Patterns.SuedeBack(texture) : null);
    }

    // Leather pads have rounded corners and the shadows follow them: radius as a share of the width, 0 for a rug.
    double cornerShare;

    /// <param name="suede">a leather pad's underside, made with the texture (both off the UI thread)</param>
    void ApplyPattern(Pattern pattern, System.Windows.Media.Imaging.BitmapSource texture, System.Windows.Media.Imaging.BitmapSource? suede)
    {
        var rugBrush = TextureBrush(texture);
        var front = new DiffuseMaterial(rugBrush);
        // The back of a hand-knotted rug shows the same pattern, flatter and darker; a desk pad
        // is plain suede underneath.
        var back = suede != null
            ? new DiffuseMaterial(TextureBrush(suede))
            : new DiffuseMaterial(rugBrush) { Color = Color.FromRgb(0xB4, 0xAA, 0xA0) };
        baseRug.Material = flapRug.Material = front;
        baseRug.BackMaterial = flapRug.BackMaterial = back;

        var corner = pattern.IsLeather ? pattern.Corner : 0;
        var reshaped = geometry.Kind != pattern.Kind || corner != cornerShare;
        geometry.Kind = pattern.Kind;
        cornerShare = corner;
        ApplyFringe(pattern);
        if (reshaped && body.Width > 0) RenderShadows();
    }

    // Fringe strands are drawn to the rug's height, so they are redrawn after a resize.
    void ApplyFringe(Pattern pattern)
    {
        // A leather pad has none (its fringe meshes are empty).
        if (pattern.IsLeather) return;
        var fringe = new DiffuseMaterial(TextureBrush(Patterns.FringeTexture(pattern.Fringe, body.Height)));
        baseFringe.Material = flapFringe.Material = fringe;
        baseFringe.BackMaterial = flapFringe.BackMaterial = fringe;
    }

    static ImageBrush TextureBrush(System.Windows.Media.Imaging.BitmapSource image)
    {
        var brush = new ImageBrush(image) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        brush.Freeze();
        return brush;
    }

    void ApplySize(double rugWidth)
    {
        SetRugSize(rugWidth, body.TopLeft);
        RenderShadows();
    }

    /// <summary>The rug's size and place in the window; any lifted corner lies down.</summary>
    void SetRugSize(double rugWidth, Point topLeft)
    {
        body = new Rect(topLeft, new Size(rugWidth, Math.Round(rugWidth / Aspect)));
        LayOutRug();

        pose.PeelCorner = pose.PeelTip = tipTarget = body.TopLeft;
        tipVelocity = default;
        peelOpen = false;
    }

    /// <summary>
    /// Carries the rug to a new place in the window: everything kept in window coordinates
    /// (a lifted corner, where it is held) goes along with it.
    /// </summary>
    void MoveRug(Point topLeft)
    {
        var shift = topLeft - body.TopLeft;
        if (shift.LengthSquared < 1e-12) return;
        body.Offset(shift);
        pose.PeelCorner += shift;
        pose.PeelTip += shift;
        pose.SwayAnchor += shift;
        tipTarget += shift;
        if (hoverCorner is { } corner) hoverCorner = corner + shift;
        LayOutRug();
    }

    /// <summary>
    /// The soft shadows, computed for the rug's exact size. Costly, so while an edge is being
    /// dragged the last ones are stretched to fit, and they are computed afresh when it is let go.
    /// </summary>
    void RenderShadows()
    {
        var corner = cornerShare * body.Width;
        AmbientShadow.Source = Patterns.SoftShadow(body.Width, body.Height, AmbientRadius, corner);
        ContactShadow.Source = Patterns.SoftShadow(body.Width, body.Height, ContactRadius, corner);
    }

    const double AmbientRadius = 16, ContactRadius = 2.5;

    /// <summary>The floor that catches clicks on the rug, and the two soft shadows, where the rug is.</summary>
    // Room the soft shadows take around the rug: the wider one's blur, and how far it falls and
    // spreads when the rug is carried.
    const double ShadowPad = 48;

    void LayOutRug()
    {
        LayOutViews();
        Floor.Margin = new Thickness(body.X, body.Y, 0, 0);
        (Floor.Width, Floor.Height) = (body.Width, body.Height);

        // The shadow layer covers just the rug and its shadows. Its fading mask, used while a
        // corner is up, makes the layer draw through a surface of its own size every frame; over
        // the whole screen that cost visible frames.
        Shadows.HorizontalAlignment = HorizontalAlignment.Left;
        Shadows.VerticalAlignment = VerticalAlignment.Top;
        Shadows.Margin = new Thickness(body.X - ShadowPad, body.Y - ShadowPad, 0, 0);
        (Shadows.Width, Shadows.Height) = (body.Width + 2 * ShadowPad, body.Height + 2 * ShadowPad);
        foreach (var (image, radius) in new[] { (AmbientShadow, AmbientRadius), (ContactShadow, ContactRadius) })
        {
            var pad = Math.Ceiling(radius * 2);
            image.Margin = new Thickness(ShadowPad - pad, ShadowPad - pad, 0, 0);
            // Exactly its own size once computed for this rug; stretched while an edge is dragged.
            image.Stretch = Stretch.Fill;
            (image.Width, image.Height) = (Math.Ceiling(body.Width) + 2 * pad, Math.Ceiling(body.Height) + 2 * pad);
        }
    }

    RugMenu? menu;

    void BuildMenu()
    {
        menu = new RugMenu(this, ScreenBody, () => Patterns.Find(storage.State.Pattern), Lay,
            () => storage.State.DailyRug, on =>
            {
                storage.State.DailyRug = on;
                // Switched on: the run starts again from the first rug of the first tab, today.
                storage.State.DailyDate = null;
                storage.State.DailyStart = on ? DateTime.Today.ToString("yyyy-MM-dd") : null;
                storage.Save();
                LayRugOfTheDay();
            },
            () => AutoStart.IsOn, AutoStart.Set,
            BringOut, () => Application.Current.Shutdown());

        // The menu gets ready a moment after the rug is out, so the first right-click is quick.
        ContentRendered += async (_, _) => { await Task.Delay(1500); menu.Prepare(); };

        // Rug of the day: on starting, and when the date changes while the rug is out.
        LayRugOfTheDay();
        var daily = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        daily.Tick += (_, _) => LayRugOfTheDay();
        daily.Start();
        // Any click on the rug puts the menu away (a right-click then opens it afresh).
        PreviewMouseDown += (_, e) => { Diag.Note($"rug PreviewMouseDown {e.ChangedButton}"); menu.Close(); };
        if (Diag.On) { Activated += (_, _) => Diag.Note("rug activated"); Deactivated += (_, _) => Diag.Note("rug deactivated"); Diag.Note($"rug window {Hwnd}"); }
        MouseRightButtonUp += (_, e) =>
        {
            if (mode != Mode.Idle) return;
            e.Handled = true;
            menu.Open();
        };
        // For checking the menu's look without a mouse: SAJJADA_MENU_TEST=1 opens it on start.
        if (Environment.GetEnvironmentVariable("SAJJADA_MENU_TEST") == "1")
            ContentRendered += (_, _) => menu.Open();
        // "late": opened after the menu got ready, the way a right-click usually finds it.
        if (Environment.GetEnvironmentVariable("SAJJADA_MENU_TEST") == "inactive")
            ContentRendered += async (_, _) => { await Task.Delay(4000); menu.Open(activate: false); };
        if (Environment.GetEnvironmentVariable("SAJJADA_MENU_TEST") == "late")
            ContentRendered += async (_, _) => { await Task.Delay(4000); menu.Open(); };
        // SAJJADA_MENU_DRAG_TEST=<folder>: drags the menu to every edge and records it (MenuTest.cs).
        if (Environment.GetEnvironmentVariable("SAJJADA_MENU_DRAG_TEST") is { } menuFolder)
            ContentRendered += (_, _) => menu.RunDragTest(menuFolder);
        if (Environment.GetEnvironmentVariable("SAJJADA_MENU_OPEN_TEST") is { } openFolder)
            ContentRendered += (_, _) => menu.RunOpenTest(openFolder);
        // For checking folds without a mouse: SAJJADA_PEEL_TEST="corner,x,y" lifts corner 0-3
        // (top-left, top-right, bottom-right, bottom-left) to x,y given as shares of the rug.
        if (Environment.GetEnvironmentVariable("SAJJADA_PEEL_TEST") is { } spec)
            ContentRendered += (_, _) => TestPeel(spec);
        // SAJJADA_MOTION_TEST=<folder>: replays every movement and records each frame (MotionTest.cs).
        if (Environment.GetEnvironmentVariable("SAJJADA_MOTION_TEST") is { } folder)
            ContentRendered += (_, _) => RunMotionTest(folder);
    }

    void TestPeel(string spec)
    {
        var a = spec.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var corner = Corners[(int)a[0]];
        StartPeel(corner);
        tipTarget = pose.PeelTip = ClampTip(corner, new Point(body.X + body.Width * a[1], body.Y + body.Height * a[2]));
        peelOpen = true;
        Redraw();
        // A fourth number lays the corner down again after that many seconds.
        if (a.Length > 3)
        {
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(a[3]) };
            later.Tick += (_, _) => { later.Stop(); ClosePeel(); };
            later.Start();
        }
    }

    int layRequest;

    /// <summary>
    /// Lays the chosen design and remembers it. The picture is read on another thread, so the
    /// rug keeps moving smoothly meanwhile; if another design is chosen before it is ready,
    /// only the newest one is laid.
    /// </summary>
    async void Lay(Pattern p)
    {
        storage.State.Pattern = p.Id;
        storage.Save();
        var ticket = ++layRequest;
        var (texture, suede) = await Task.Run(() =>
        {
            var t = Patterns.Texture(p);
            return (t, p.IsLeather ? Patterns.SuedeBack(t) : null);
        });
        if (ticket != layRequest) return;
        ApplyPattern(p, texture, suede);
        Redraw();
    }

    /// <summary>With "rug of the day" on, a new design once a day. A design chosen by hand stays until the next day.</summary>
    void LayRugOfTheDay()
    {
        var s = storage.State;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!s.DailyRug || s.DailyDate == today) return;
        s.DailyDate = today;
        // Switched on by an older version, which kept no start: the run starts today.
        if (!DateTime.TryParseExact(s.DailyStart, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var start))
        {
            start = DateTime.Today;
            s.DailyStart = today;
        }
        Lay(Patterns.OfTheDay(DateTime.Today, start));
    }

    // ---------- animation ----------

    void Animate()
    {
        if (rendering) return;
        rendering = true;
        lastFrame = TimeSpan.Zero;
        lastRugSpot = body.TopLeft;
        CompositionTarget.Rendering += OnFrame;
    }

    // Where the hand last asked the rug to be (carried) or how big (resized). Mouse moves come
    // more often than frames; the newest is laid once per frame, rug, floor and shadows together.
    Point? carryTo;
    Rect? resizeTo;

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - lastFrame).TotalSeconds, 1 / 30.0);
        if (dt <= 0) return;
        lastFrame = now;

        if (carryTo is { } spot)
        {
            MoveRug(spot);
            carryTo = null;
        }
        if (resizeTo is { } size)
        {
            SetRugSize(size.Width, size.TopLeft);
            resizeTo = null;
        }

        // The corner is a spring: firm under the hand, softer and slightly springy when let go.
        var (k, zeta) = mode == Mode.Peeling ? (700.0, 0.8) : (240.0, 0.62);
        var accel = (tipTarget - pose.PeelTip) * k - tipVelocity * (2 * zeta * Math.Sqrt(k));
        tipVelocity += accel * dt;
        pose.PeelTip += tipVelocity * dt;
        if ((tipTarget - pose.PeelTip).Length < 0.15 && tipVelocity.Length < 3)
        {
            pose.PeelTip = tipTarget;
            tipVelocity = default;
        }
        // A springy corner coming home must not swing past the edge: the fold would turn
        // inside out and the rug would flip over for a moment.
        if (Vector.Multiply(pose.PeelTip - pose.PeelCorner, Inward(pose.PeelCorner)) < 0)
        {
            pose.PeelTip = pose.PeelCorner;
            tipVelocity = default;
        }

        Sway(dt);

        carryLift += ((mode == Mode.Carrying ? 1 : 0) - carryLift) * (1 - Math.Exp(-dt * 10));

        Redraw();

        var settled = pose.PeelTip == tipTarget && pose.Sway.Length < 0.05 && swayVelocity.Length < 0.5 && velocity.Length < 1
                      && mode is not (Mode.Carrying or Mode.Resizing) && carryLift < 0.01;
        if (settled)
        {
            if (pose.PeelTip == pose.PeelCorner) peelOpen = false;
            pose.Sway = swayVelocity = default;
            CompositionTarget.Rendering -= OnFrame;
            rendering = false;
            Redraw();
        }
    }

    /// <summary>
    /// The rug has weight: while carried, the parts away from the hand trail behind in
    /// proportion to the hand's speed; when the hand stops they swing back and settle.
    /// Speed is smoothed because mouse moves arrive unevenly between frames.
    /// </summary>
    void Sway(double dt)
    {
        var at = body.TopLeft;
        var raw = mode == Mode.Carrying ? (at - lastRugSpot) / dt : default;
        lastRugSpot = at;
        velocity += (raw - velocity) * (1 - Math.Exp(-dt / 0.08));

        var trail = -velocity * 0.015;
        const double max = 16;
        if (trail.Length > max) trail *= max / trail.Length;

        const double k = 170, zeta = 0.55;
        var accel = (trail - pose.Sway) * k - swayVelocity * (2 * zeta * Math.Sqrt(k));
        swayVelocity += accel * dt;
        pose.Sway += swayVelocity * dt;
    }
    void Redraw()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        RedrawScene();
        lastDrawMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    void RedrawScene()
    {
        geometry.Update(body, pose);
        FlapShadow.Data = geometry.FlapShadow(pose);
        FadeShadow(geometry.FoldFor(body, pose));

        // Carried, the rug rises: its shadow spreads and falls further away.
        AmbientShadow.Opacity = 0.30 - 0.06 * carryLift;
        AmbientScale.ScaleX = AmbientScale.ScaleY = 1 + 0.025 * carryLift;
        AmbientOffset.Y = 5 + 7 * carryLift;
        ContactShadow.Opacity = 0.45 * (1 - carryLift);
    }

    /// <summary>
    /// Where the corner is up there is no rug left to cast a shadow on the desk. The shadow
    /// fades out across the fold line, so the uncovered desk looks like the rest of the desk.
    /// </summary>
    readonly LinearGradientBrush shadeMask = new(Colors.Black, Colors.Transparent, 0) { MappingMode = BrushMappingMode.Absolute };

    /// <summary>The pointer's shape, set only when it changes (the mouse moves far more often).</summary>
    void SetCursor(Cursor? c)
    {
        if (Cursor != c) Cursor = c;
    }

    void FadeShadow(Fold fold)
    {
        if (!fold.Active || fold.R <= 0)
        {
            Shadows.OpacityMask = null;
            return;
        }
        // In the shadow layer's own coordinates, which start ShadowPad above and left of the rug.
        var layer = new Vector(body.X - ShadowPad, body.Y - ShadowPad);
        var onLine = fold.Corner + fold.Dir * fold.Line - layer;
        var uncovered = onLine - fold.Dir * Math.Max(fold.R * 2.5, 12);
        // One brush, moved each frame, rather than a new one every frame.
        shadeMask.StartPoint = onLine;
        shadeMask.EndPoint = uncovered;
        if (Shadows.OpacityMask != shadeMask) Shadows.OpacityMask = shadeMask;
    }
    // ---------- corners and edges ----------

    Point[] Corners => new[] { body.TopLeft, body.TopRight, body.BottomRight, body.BottomLeft };

    double CornerZone => Math.Clamp(Math.Min(body.Width, body.Height) * 0.2, 32, 72);

    Point? CornerAt(Point p)
    {
        if (!Rect.Inflate(body, RugGeometry.FringeLength, 6).Contains(p)) return null;
        foreach (var c in Corners)
            if (Math.Abs(p.X - c.X) < CornerZone && Math.Abs(p.Y - c.Y) < CornerZone) return c;
        return null;
    }

    Edge EdgeAt(Point p)
    {
        if (CornerAt(p) is not null) return Edge.None;
        bool alongX = p.X > body.Left && p.X < body.Right, alongY = p.Y > body.Top && p.Y < body.Bottom;
        // The short sides carry the fringe, so their band reaches over it.
        if (alongY && p.X > body.Left - RugGeometry.FringeLength && p.X < body.Left + EdgeIn) return Edge.Left;
        if (alongY && p.X < body.Right + RugGeometry.FringeLength && p.X > body.Right - EdgeIn) return Edge.Right;
        if (alongX && p.Y > body.Top - EdgeOut && p.Y < body.Top + EdgeIn) return Edge.Top;
        if (alongX && p.Y < body.Bottom + EdgeOut && p.Y > body.Bottom - EdgeIn) return Edge.Bottom;
        return Edge.None;
    }

    Point NearestCorner(Point p) => Corners.OrderBy(c => (c - p).Length).First();

    Vector Inward(Point corner)
    {
        var v = new Point(body.X + body.Width / 2, body.Y + body.Height / 2) - corner;
        v.Normalize();
        return v;
    }

    void StartPeel(Point corner)
    {
        if (pose.PeelCorner != corner)
        {
            pose.PeelCorner = pose.PeelTip = corner;
            tipVelocity = default;
        }
    }

    /// <summary>
    /// Keeps the pull short of folding the rug in half, and the corner on the rug itself:
    /// a corner pulled off the rug would fold part of it out of the window, where it is cut off.
    /// </summary>
    Point ClampTip(Point corner, Point wanted)
    {
        var v = wanted - corner;
        var inward = Inward(corner);
        if (Vector.Multiply(v, inward) < 0) return corner;
        var max = Math.Min(body.Width, body.Height) * 0.95;
        if (v.Length > max) v *= max / v.Length;
        var inside = Rect.Inflate(body, -6, -6);
        var tip = corner + v;
        return new Point(Math.Clamp(tip.X, inside.Left, inside.Right), Math.Clamp(tip.Y, inside.Top, inside.Bottom));
    }

    bool Flat => (pose.PeelTip - pose.PeelCorner).Length < 0.5;

    /// <summary>
    /// Lifts a corner slightly. Another corner that is still up is first laid down;
    /// switching straight away would make it snap flat.
    /// </summary>
    void Peek(Point corner, double amount)
    {
        if (corner != pose.PeelCorner && !Flat)
        {
            tipTarget = pose.PeelCorner;
            Animate();
            return;
        }
        StartPeel(corner);
        tipTarget = corner + Inward(corner) * amount;
        Animate();
    }

    void ClosePeel()
    {
        peelOpen = false;
        tipTarget = pose.PeelCorner;
        Animate();
    }

    // ---------- mouse ----------

    // The pointer in physical screen pixels. The motion test replays movements through it
    // without touching the real mouse.
    Func<Point> cursorAt = Native.CursorPos;

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        Down(e.GetPosition(this));
    }

    void OnMove(object sender, MouseEventArgs e) => Move(e.GetPosition(this));

    void OnUp(object sender, MouseButtonEventArgs e) => Up();

    void Down(Point p)
    {
        hoverTimer.Stop();
        CaptureMouse();

        if (peelOpen)
        {
            // A folded rug stays folded: grab the folded part to fold it further or lay it back.
            var fold = geometry.FoldFor(body, pose);
            if ((pose.PeelTip - p).Length < CornerZone || fold.OnFlap(p, body))
            {
                mode = Mode.Peeling;
                grabOffset = pose.PeelTip - p;
            }
            else if (body.Contains(p) && fold.Uncovers(p)) ReleaseMouseCapture();
            else StartPress(p);
            return;
        }

        if (EdgeAt(p) is var edge and not Edge.None)
        {
            mode = Mode.Resizing;
            resizeEdge = edge;
            dragFrom = (cursorAt(), body.TopLeft, body.Width);
            resizeStart = body;
            return;
        }

        if (CornerAt(p) is { } corner)
        {
            StartPeel(corner);
            mode = Mode.Peeling;
            grabOffset = corner - p;
            return;
        }

        StartPress(p);
    }

    void StartPress(Point p)
    {
        mode = Mode.Pressing;
        pressedAt = p;
    }

    void Move(Point p)
    {
        switch (mode)
        {
            case Mode.Peeling:
                tipTarget = ClampTip(pose.PeelCorner, p + grabOffset);
                Animate();
                return;

            case Mode.Pressing:
                var moved = p - pressedAt;
                if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4) return;
                mode = Mode.Carrying;
                dragFrom = (cursorAt(), body.TopLeft, body.Width);
                carryFrom = RugOnScreen();
                pose.SwayAnchor = pressedAt;
                goto case Mode.Carrying;

            case Mode.Carrying:
                {
                    // Worked out on the screen, in physical pixels: the rug follows the hand by
                    // as much as the hand moved, whichever screen the hand is on.
                    var cursor = cursorAt();
                    var rugOnScreen = carryFrom + (cursor - dragFrom.Cursor);
                    // Carried onto another screen: the window goes to cover that one. The only
                    // time it moves, and only with more than one screen.
                    if (!screen.Contains(cursor))
                    {
                        CoverScreen(Native.ScreenAt(cursor).Whole);
                        MoveRug(KeepOnScreen(ToWindow(rugOnScreen), body.Size));
                    }
                    carryTo = KeepOnScreen(ToWindow(rugOnScreen), body.Size);
                    Animate();
                    return;
                }

            case Mode.Resizing:
                Resize(CursorShift());
                return;
        }

        UpdateHover(p);
    }

    // Where the rug was on the screen when it was picked up, and its place and size when an edge was.
    Point carryFrom;
    Rect resizeStart;
    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>The rug's top-left on the screen, in physical pixels.</summary>
    Point RugOnScreen() => new(screen.X + body.X * screenDpi.DpiScaleX, screen.Y + body.Y * screenDpi.DpiScaleY);

    Vector CursorShift()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = cursorAt();
        return new Vector((cursor.X - dragFrom.Cursor.X) / dpi.DpiScaleX, (cursor.Y - dragFrom.Cursor.Y) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Grows or shrinks from the dragged edge while the opposite edge stays put,
    /// keeping the rug's 3:2 shape so its pattern is never stretched.
    /// </summary>
    void Resize(Vector shift)
    {
        var startW = dragFrom.Width;
        var wanted = resizeEdge switch
        {
            Edge.Right => startW + shift.X,
            Edge.Left => startW - shift.X,
            Edge.Bottom => startW + shift.Y * Aspect,
            _ => startW - shift.Y * Aspect,
        };
        var newW = Math.Round(Math.Clamp(wanted, RugMinWidth, RugMaxWidth));
        if (Math.Abs(newW - body.Width) < 1 && resizeTo is null) return;

        // The opposite edge stays where it was when the edge was grabbed, and the rug stays
        // centred along it; the whole rug stays on the screen.
        var c = resizeStart;
        var newH = Math.Round(newW / Aspect);
        var topLeft = resizeEdge switch
        {
            Edge.Left => new Point(c.Right - newW, c.Y + (c.Height - newH) / 2),
            Edge.Right => new Point(c.X, c.Y + (c.Height - newH) / 2),
            Edge.Top => new Point(c.X + (c.Width - newW) / 2, c.Bottom - newH),
            _ => new Point(c.X + (c.Width - newW) / 2, c.Y),
        };
        var size = new Size(newW, newH);
        resizeTo = new Rect(KeepOnScreen(topLeft, size), size);
        Animate();
    }

    void UpdateHover(Point p)
    {
        if (peelOpen)
        {
            // On the uncovered desk the cursor is the desktop's own; the hand is only for the folded part.
            var fold = geometry.FoldFor(body, pose);
            var onDesk = body.Contains(p) && fold.Uncovers(p);
            SetCursor(!onDesk && ((pose.PeelTip - p).Length < CornerZone || fold.OnFlap(p, body)) ? Cursors.Hand : null);
            return;
        }

        var edge = EdgeAt(p);
        if (edge != Edge.None)
        {
            SetCursor(edge is Edge.Left or Edge.Right ? Cursors.SizeWE : Cursors.SizeNS);
            if (hoverCorner is not null) LowerHoverCorner();
            return;
        }

        // Resting on a corner lifts it a little: that corner can be picked up.
        var hover = CornerAt(p);
        SetCursor(hover is not null ? Cursors.Hand : null);
        if (hover == hoverCorner) return;
        if (hover is { } c)
        {
            hoverCorner = c;
            hoverTimer.Stop();
            hoverTimer.Start();
        }
        else LowerHoverCorner();
    }

    void LowerHoverCorner()
    {
        hoverCorner = null;
        hoverTimer.Stop();
        if (peelOpen) return;
        tipTarget = pose.PeelCorner;
        Animate();
    }

    void Up()
    {
        var was = mode;
        mode = Mode.Idle;
        hoverCorner = null;
        hoverTimer.Stop();
        // A place or size asked for since the last frame is laid now, before it is saved.
        if (carryTo is { } spot) { MoveRug(spot); carryTo = null; }
        if (resizeTo is { } size) { SetRugSize(size.Width, size.TopLeft); resizeTo = null; }

        switch (was)
        {
            case Mode.Peeling:
                // Pulled far enough, the corner stays up until it is laid back down.
                if ((tipTarget - pose.PeelCorner).Length > Math.Min(body.Width, body.Height) * 0.22) peelOpen = true;
                else ClosePeel();
                break;

            case Mode.Carrying:
                SaveBounds();
                Animate();
                break;

            case Mode.Resizing:
                storage.State.Width = body.Width;
                SaveBounds();
                ApplyFringe(Patterns.Find(storage.State.Pattern));
                RenderShadows();
                Animate();
                break;
        }
        // Last: releasing the capture sends a mouse move that must see the final state.
        ReleaseMouseCapture();
    }

    void OnLeave(object sender, MouseEventArgs e)
    {
        if (mode != Mode.Idle || peelOpen || hoverCorner is null) return;
        LowerHoverCorner();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && peelOpen) ClosePeel();
    }

    // ---------- desktop icons under the rug ----------
    //
    // Nothing is moved on disk. An icon "under the rug" is an ordinary desktop icon whose
    // picture Explorer draws where the rug lies, so the rug covers it. Lifting a corner shows
    // the real icons, and they can be opened or dragged out as on any other part of the desktop.
    // Like a real rug, it never carries what lies under it: moved or resized, the icons stay
    // where they are on the desktop, and whatever it is laid over is covered.

    DropTarget? dropTarget;

    void ListenForIcons(IntPtr hwnd)
    {
        dropTarget = new DropTarget(hwnd)
        {
            Over = IconsOver,
            Left = IconsLeft,
            Dropped = IconsDropped,
            FromScreen = PointFromScreen,
        };
        Closed += (_, _) => dropTarget.Unregister();
    }

    /// <summary>The rug's area in screen pixels, the space Explorer's desktop works in.</summary>
    Rect ScreenBody() => new(PointToScreen(body.TopLeft), PointToScreen(body.BottomRight));

    // Only icons from the desktop may go under: anything else would have to be moved there.
    bool IconsOver(string[] paths, Point p)
    {
        if (!paths.All(DesktopIcons.OnDesktop)) return false;
        if (!peelOpen) Peek(NearestCorner(p), 34);
        return true;
    }

    void IconsLeft()
    {
        if (!peelOpen) ClosePeel();
    }

    bool IconsDropped(string[] paths, Point at)
    {
        // The desktop is still finishing its drag when this returns; its icons are asked
        // for a moment later, once it is free to answer.
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            HideIcons(paths, at);
        };
        later.Start();
        if (!peelOpen) ClosePeel();
        return true;
    }

    void HideIcons(string[] paths, Point at)
    {
        using var desk = DesktopIcons.Open();
        if (desk is null) return;
        if (desk.AutoArrange)
        {
            MessageBox.Show(Text.AutoArrange, Text.AppName, MessageBoxButton.OK, MessageBoxImage.Information,
                            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return;
        }
        var byName = new Dictionary<string, DesktopIcons.Icon>(StringComparer.OrdinalIgnoreCase);
        foreach (var icon in desk.Items()) byName.TryAdd(icon.Name, icon);
        var icons = paths.Select(p => byName.GetValueOrDefault(p)).OfType<DesktopIcons.Icon>().ToList();
        if (icons.Count == 0) return;

        // Several at once lie side by side around the spot where they were dropped.
        var dpi = VisualTreeHelper.GetDpi(this);
        var cell = desk.Spacing();
        double cellW = cell.X / dpi.DpiScaleX, cellH = cell.Y / dpi.DpiScaleY;
        var cols = (int)Math.Ceiling(Math.Sqrt(icons.Count));
        var rows = (int)Math.Ceiling(icons.Count / (double)cols);
        var first = at - new Vector((cols - 1) * cellW / 2, (rows - 1) * cellH / 2);

        var before = storage.State.Before;
        var screen = ScreenBody();
        var spots = new List<DesktopIcons.POINT>();
        for (var k = 0; k < icons.Count; k++)
        {
            var c = first + new Vector(k % cols * cellW, k / cols * cellH);
            var share = new[] { Math.Clamp((c.X - body.X) / body.Width, 0, 1), Math.Clamp((c.Y - body.Y) / body.Height, 0, 1) };
            // Remember where it lay on the desktop, unless it is just being moved around under the rug.
            if (!Under(desk, icons[k], screen, cell)) before[icons[k].Name] = new[] { icons[k].Pos.X, icons[k].Pos.Y };
            spots.Add(desk.FromScreen(Spot(screen, share, cell)));
        }
        try { desk.Place(icons, spots); }
        catch (COMException) { }
        storage.Save();
    }

    /// <summary>True when all of an icon's cell lies under the rug.</summary>
    static bool Under(DesktopIcons desk, DesktopIcons.Icon icon, Rect rug, DesktopIcons.POINT cell)
    {
        var at = desk.ToScreen(icon.Pos);
        return rug.Contains(new Rect(at.X, at.Y, cell.X, cell.Y));
    }

    /// <summary>
    /// Top-left for an icon's cell so that all of it stays under the rug. Half a cell of room is
    /// kept on each side, because "align icons to grid" may still nudge it to the nearest cell.
    /// </summary>
    static Point Spot(Rect rug, double[] share, DesktopIcons.POINT cell)
    {
        double Axis(double start, double length, double size, double s)
        {
            double lo = start + size / 2, hi = start + length - size * 1.5;
            if (hi < lo) return start + (length - size) / 2;
            return Math.Clamp(start + s * length - size / 2, lo, hi);
        }
        return new Point(Axis(rug.X, rug.Width, cell.X, share[0]), Axis(rug.Y, rug.Height, cell.Y, share[1]));
    }

    /// <summary>
    /// Every icon under the rug right now comes out: back where it was before it was dropped on
    /// the rug, or, for one the rug was simply laid over, just beside the rug.
    /// </summary>
    void BringOut()
    {
        using var desk = DesktopIcons.Open();
        if (desk is null) return;
        try
        {
            var screen = ScreenBody();
            var cell = desk.Spacing();
            var before = storage.State.Before;
            var icons = desk.Items().Where(i => Rect.Intersect(new Rect(desk.ToScreen(i.Pos), new Size(cell.X, cell.Y)), screen) is { IsEmpty: false } r
                                                && r.Width * r.Height > cell.X * cell.Y / 2).ToList();
            var beside = 0;
            var spots = icons.Select(icon => before.TryGetValue(icon.Name, out var b)
                ? new DesktopIcons.POINT { X = b[0], Y = b[1] }
                : desk.FromScreen(new Point(screen.Right + cell.X / 4, screen.Top + beside++ * cell.Y))).ToList();
            desk.Place(icons, spots);
            before.Clear();
            storage.Save();
        }
        catch (COMException) { }
    }

    // ---------- position ----------

    void SaveBounds()
    {
        // Kept as the spot of a window with 48 px of room around the rug, as every version reads it.
        var rug = RugOnScreen();
        var dpi = screenDpi;
        (storage.State.ScreenX, storage.State.ScreenY) =
            ((int)Math.Round(rug.X - SavedGutter * dpi.DpiScaleX), (int)Math.Round(rug.Y - SavedGutter * dpi.DpiScaleY));
        (storage.State.Left, storage.State.Top) = (rug.X / dpi.DpiScaleX - SavedGutter, rug.Y / dpi.DpiScaleY - SavedGutter);
        storage.Save();
    }

    /// <summary>Up to 60% of the work area's width and 65% of its height, never past the largest rug.</summary>
    static double FirstWidth()
    {
        var area = SystemParameters.WorkArea;
        var wanted = Math.Min(area.Width * 0.6, area.Height * 0.65 * Aspect);
        return Math.Round(Math.Clamp(wanted, RugMinWidth, RugMaxWidth));
    }

    static bool FitsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
}
