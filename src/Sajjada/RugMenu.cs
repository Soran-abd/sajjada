using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Sajjada;

/// <summary>
/// The rug's right-click menu: a dark glass card in Arabic, right to left. Its heart is a gallery
/// of every design as a small picture, in four tabs; choosing one lays that rug at once and the
/// menu stays open, so designs can be tried one after another. The app's actions sit underneath.
/// </summary>
sealed partial class RugMenu
{
    // ---------- look ----------

    static readonly Brush CardFill = Frozen(new LinearGradientBrush(
        Color.FromArgb(0xF5, 0x24, 0x25, 0x2B), Color.FromArgb(0xF5, 0x17, 0x18, 0x1C), 90));
    static readonly Brush CardEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0xF0, 0xE9)));
    static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x98, 0x92)));
    static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Gold = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xB2, 0x4C)));
    static readonly Brush GoldSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xE9, 0xB2, 0x4C)));
    static readonly Brush GoldInk = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x1E, 0x08)));
    static readonly Brush Danger = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x7F)));
    static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Four pictures to a row; the gallery always has room for three rows, so the card keeps its size between tabs.
    const double ThumbW = 100, ThumbH = 66, CellW = 118, CellH = 108;
    const int Columns = 4, Rows = 3;

    // ---------- parts ----------

    readonly Window window;
    readonly IntPtr hwnd;
    readonly Border card;
    readonly ScaleTransform cardScale = new(1, 1);
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 12) };
    readonly WrapPanel gallery = new() { ItemWidth = CellW, ItemHeight = CellH, Width = CellW * Columns, Height = CellH * Rows };
    readonly TextBlock currentName = new() { Foreground = Gold, FontSize = 12, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight };
    readonly Dictionary<string, BitmapSource> thumbs = new();
    readonly Func<Pattern> selected;
    readonly Action<Pattern> choose;
    readonly FrameworkElement owner;
    readonly Func<Rect> rugOnScreen;
    readonly List<Action> toggleRefreshers = new();
    PatternGroup shown = Patterns.Groups[0];

    // Room around the card inside the window, for its shadow.
    const double CardMargin = 22;

    // Dragging the menu by its grip or title, in physical screen pixels like the rug itself.
    bool dragging;
    Point dragFrom;
    Native.POINT windowFrom;

    /// <param name="rugOnScreen">the rug's area in physical screen pixels, to open the menu beside it</param>
    public RugMenu(FrameworkElement owner, Func<Rect> rugOnScreen, Func<Pattern> selected, Action<Pattern> choose,
                   Func<bool> dailyOn, Action<bool> setDaily, Func<bool> startupOn, Action<bool> setStartup,
                   Action bringOut, Action close)
    {
        this.owner = owner;
        this.rugOnScreen = rugOnScreen;
        this.selected = selected;
        this.choose = choose;

        var content = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        content.Children.Add(DragArea());
        content.Children.Add(tabs);
        content.Children.Add(gallery);
        content.Children.Add(new Rectangle { Height = 1, Fill = Line, Margin = new Thickness(2, 10, 2, 6) });
        content.Children.Add(ToggleRow("", Text.DailyRug, dailyOn, setDaily));
        content.Children.Add(ToggleRow("", Text.StartWithWindows, startupOn, setStartup));
        content.Children.Add(new Rectangle { Height = 1, Fill = Line, Margin = new Thickness(2, 6, 2, 6) });
        content.Children.Add(ActionRow("", Text.BringOut, bringOut, false));
        content.Children.Add(ActionRow("", Text.Close, close, true));
        TextElement.SetFontFamily(content, Font);

        card = new Border
        {
            Background = CardFill, BorderBrush = CardEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 18, 20, 10),
            Margin = new Thickness(CardMargin),
            Child = content, Focusable = true,
            RenderTransform = cardScale, RenderTransformOrigin = new Point(0.5, 0),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        card.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MakeDraggable();

        // A window of its own, not a WPF popup: a popup keeps itself on the screen by flipping to
        // the other side of its point when it nears an edge, so dragged to an edge it jumped
        // hundreds of pixels back. This window goes exactly where it is put; Fit keeps the card on
        // the screen by sliding it along the edge. It closes itself (Escape, a click on the rug,
        // or switching to another program) rather than on any click, so dragging it does not close it.
        window = new Window
        {
            Content = card, Title = Text.AppName,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false, Topmost = true,
        };
        hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Application.Current.Deactivated += (_, _) => { Diag.Note("app deactivated"); Close(); };
        if (Diag.On)
        {
            Diag.Note($"menu window {hwnd}");
            Application.Current.Activated += (_, _) => Diag.Note("app activated");
            window.Activated += (_, _) => Diag.Note("menu activated");
            window.Deactivated += (_, _) => Diag.Note("menu deactivated");
            window.PreviewMouseLeftButtonDown += (_, e) => Diag.Note($"menu PreviewMouseLeftButtonDown on {e.OriginalSource.GetType().Name}");
            System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                var name = msg switch { 0x21 => "WM_MOUSEACTIVATE", 0x06 => "WM_ACTIVATE", 0x201 => "WM_LBUTTONDOWN", 0x202 => "WM_LBUTTONUP", 0x215 => "WM_CAPTURECHANGED", 0x1C => "WM_ACTIVATEAPP", 0xA1 => "WM_NCLBUTTONDOWN", _ => null };
                if (name != null) Diag.Note($"{name} w={w}");
                return IntPtr.Zero;
            });
        }
        card.Opacity = 0;
    }

    /// <summary>
    /// Gets everything ready a moment after the program starts, so the first right-click only has
    /// to show the menu: every small picture decoded on another thread, and the window drawn once,
    /// fully transparent, out of sight (the first drawing of a window is the slow one).
    /// </summary>
    public async void Prepare()
    {
        var width = (int)(ThumbW * 2.2);
        var ready = await Task.Run(() => Patterns.All.Select(p => (p.Id, Image: Patterns.Thumbnail(p, width))).ToList());
        foreach (var (id, image) in ready) thumbs.TryAdd(id, image);
        if (window.IsVisible || opened) return;

        Show(Patterns.GroupOf(selected()));
        window.ShowActivated = false;
        Native.MoveWindow(hwnd, -32000, -32000);
        window.Show();
        await Frames(3);
        // Opened meanwhile: it is already where it belongs, leave it.
        if (!opened) window.Hide();
        window.ShowActivated = true;
    }

    bool opened, closing;
    // Counts closings, so a closing that is overtaken by an opening does not hide the menu.
    int closings;

    public void Open(bool activate = true)
    {
        Diag.Note("Open");
        opened = true;
        closing = false;
        closings++;
        window.ShowActivated = activate;
        currentName.Text = selected().Name;
        Show(Patterns.GroupOf(selected()));
        foreach (var refresh in toggleRefreshers) refresh();

        // Beside the rug, so the whole rug stays in view while designs are tried; at the cursor
        // only when there is no room on any side. All in physical screen pixels.
        var dpi = VisualTreeHelper.GetDpi(owner);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(card.DesiredSize.Width * dpi.DpiScaleX, card.DesiredSize.Height * dpi.DpiScaleY);
        var shadow = CardMargin * dpi.DpiScaleX;
        var rug = rugOnScreen();
        var at = Beside(rug, Native.WorkAreaOf(rug), size, shadow, gap: 26 * dpi.DpiScaleX)
                 ?? cursorAt() - new Vector(shadow, shadow);
        at = Fit(at, size, shadow, cursorAt());
        // The fade starts before the window shows, so its first picture is already the faint one.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            cardScale.BeginAnimation(axis, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        // Put in place while still hidden, so it never shows anywhere else first.
        Native.MoveWindow(hwnd, (int)Math.Round(at.X), (int)Math.Round(at.Y));
        if (!window.IsVisible) window.Show();
        if (activate) window.Activate();
        card.Focus();
    }

    /// <summary>
    /// Where the window goes when asked to be at <paramref name="at"/>, so that the card (the
    /// window without its shadow's margin) stays whole on the screens: anywhere it is wholly on
    /// usable screen area, even across two screens; otherwise pushed back inside the usable area
    /// of the screen under the pointer, sliding along the edge it met. Physical pixels.
    /// </summary>
    static Point Fit(Point at, Size window, double shadow, Point pointer)
    {
        var card = new Rect(at.X + shadow, at.Y + shadow, Math.Max(1, window.Width - 2 * shadow), Math.Max(1, window.Height - 2 * shadow));
        if (Native.InWorkArea(card.TopLeft) && Native.InWorkArea(new Point(card.Right - 1, card.Top))
            && Native.InWorkArea(new Point(card.Left, card.Bottom - 1)) && Native.InWorkArea(new Point(card.Right - 1, card.Bottom - 1)))
            return at;
        var work = Native.ScreenAt(pointer).Work;
        // A card larger than the screen keeps its top-left corner on it.
        var x = Math.Clamp(card.X, work.Left, Math.Max(work.Left, work.Right - card.Width));
        var y = Math.Clamp(card.Y, work.Top, Math.Max(work.Top, work.Bottom - card.Height));
        return new Point(x - shadow, y - shadow);
    }

    /// <summary>
    /// Where the menu window goes so that the card stands next to the rug: on the side with more room,
    /// else below or above it; null when it fits nowhere. The window is larger than the card by
    /// the shadow's margin on every side.
    /// </summary>
    static Point? Beside(Rect rug, Rect work, Size menu, double shadow, double gap)
    {
        double cardW = menu.Width - 2 * shadow, cardH = menu.Height - 2 * shadow;
        double ClampX(double x) => Math.Clamp(x, work.Left - shadow, Math.Max(work.Left - shadow, work.Right - menu.Width + shadow));
        double ClampY(double y) => Math.Clamp(y, work.Top - shadow, Math.Max(work.Top - shadow, work.Bottom - menu.Height + shadow));

        var roomRight = work.Right - rug.Right - gap;
        var roomLeft = rug.Left - work.Left - gap;
        var middleY = ClampY(rug.Top + rug.Height / 2 - menu.Height / 2);
        if (roomRight >= cardW && roomRight >= roomLeft) return new Point(rug.Right + gap - shadow, middleY);
        if (roomLeft >= cardW) return new Point(rug.Left - gap - cardW - shadow, middleY);
        if (roomRight >= cardW) return new Point(rug.Right + gap - shadow, middleY);

        var middleX = ClampX(rug.Left + rug.Width / 2 - menu.Width / 2);
        if (work.Bottom - rug.Bottom - gap >= cardH) return new Point(middleX, rug.Bottom + gap - shadow);
        if (rug.Top - work.Top - gap >= cardH) return new Point(middleX, rug.Top - gap - cardH - shadow);
        return null;
    }

    /// <summary>
    /// Fades the card out, and hides the window only once a fully transparent picture of it has
    /// reached the screen. A see-through window keeps its last picture while hidden and shows that
    /// again the moment it is shown, before anything new is drawn: hidden while visible, the old
    /// menu flashed up whole for a frame on the next opening, then vanished and faded in.
    /// </summary>
    public void Close()
    {
        Diag.Note($"Close visible={window.IsVisible} closing={closing}");
        if (!window.IsVisible || closing) return;
        closing = true;
        var ticket = ++closings;
        dragging = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += async (_, _) =>
        {
            await Frames(3);
            if (ticket != closings) return;
            window.Hide();
            closing = false;
        };
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // The pointer in physical screen pixels; a test puts a pretend one here.
    internal Func<Point> cursorAt = Native.CursorPos;
    FrameworkElement? dragArea;

    void BeginDrag()
    {
        Diag.Note("BeginDrag");
        dragging = true;
        dragFrom = cursorAt();
        windowFrom = Native.WindowPos(hwnd);
    }

    // The window follows the pointer pixel for pixel; at an edge the card stops against it and
    // waits there until the pointer comes back to the spot it was holding.
    void DragOn()
    {
        if (!dragging) return;
        var now = cursorAt();
        var size = Native.WindowSize(hwnd);
        var shadow = CardMargin * VisualTreeHelper.GetDpi(card).DpiScaleX;
        var want = new Point(windowFrom.X + now.X - dragFrom.X, windowFrom.Y + now.Y - dragFrom.Y);
        var at = Fit(want, new Size(size.X, size.Y), shadow, now);
        Native.MoveWindow(hwnd, (int)Math.Round(at.X), (int)Math.Round(at.Y));
    }

    void EndDrag() => dragging = false;

    IntPtr Handle => hwnd;

    Rectangle? grip;

    /// <summary>The grip and the title, with the move pointer: the plainest place to carry the menu by.</summary>
    UIElement DragArea()
    {
        grip = new Rectangle
        {
            Width = 38, Height = 4, RadiusX = 2, RadiusY = 2, Fill = Muted, Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -6, 0, 12),
        };
        var area = new Border
        {
            // Transparent, not empty, so the space between the words can be grabbed too.
            Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            Child = new StackPanel { Children = { grip, Header() } },
        };
        area.MouseEnter += (_, _) => grip.Opacity = 0.9;
        area.MouseLeave += (_, _) => { if (!dragging) grip.Opacity = 0.45; };
        dragArea = area;
        return area;
    }

    /// <summary>
    /// Pressing anywhere on the card that is not something to click (a design, a tab, a switch,
    /// an action) and moving carries the whole menu. Only the title used to: a press on the empty
    /// parts of the card did nothing, so the menu seemed to need a second try.
    /// </summary>
    void MakeDraggable()
    {
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (Clickable(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            BeginDrag();
            var got = card.CaptureMouse();
            Diag.Note($"card CaptureMouse -> {got}");
            if (grip != null) grip.Opacity = 0.9;
        };
        card.MouseMove += (_, _) => DragOn();
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            e.Handled = true;
            // Where the button came up, even if no move was reported for that last stretch.
            DragOn();
            card.ReleaseMouseCapture();
        };
        card.LostMouseCapture += (_, _) =>
        {
            Diag.Note("card LostMouseCapture");
            dragging = false;
            if (grip != null && dragArea?.IsMouseOver != true) grip.Opacity = 0.45;
        };
    }

    // The clickable parts of the menu are the ones with the hand pointer.
    bool Clickable(DependencyObject? d)
    {
        for (; d != null && d != card; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Cursor: { } c } && c == Cursors.Hand) return true;
        return false;
    }

    // ---------- header: name of the app, a word of help, and the design on the rug now ----------

    UIElement Header()
    {
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Text.AppName, Foreground = Ink, FontSize = 19, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = Text.Subtitle, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 1, 0, 0) });

        var dot = new Ellipse { Width = 7, Height = 7, Fill = Gold, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            Background = GoldSoft, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 5),
            VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, currentName } },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(titles);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
        return grid;
    }

    // ---------- tabs and gallery ----------

    void Show(PatternGroup group)
    {
        shown = group;
        tabs.Children.Clear();
        foreach (var g in Patterns.Groups) tabs.Children.Add(Tab(g));
        gallery.Children.Clear();
        foreach (var p in group.Patterns) gallery.Children.Add(Tile(p));
    }

    UIElement Tab(PatternGroup group)
    {
        var on = group == shown;
        var label = new TextBlock
        {
            Text = group.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = on ? Gold : Muted,
        };
        var count = new TextBlock
        {
            Text = group.Patterns.Length.ToString(), FontSize = 11, Margin = new Thickness(6, 1, 0, 0),
            Foreground = on ? Gold : Muted, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
        };
        var tab = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 6, 12, 7), Margin = new Thickness(0, 0, 4, 0),
            Background = on ? GoldSoft : Brushes.Transparent, Cursor = Cursors.Hand,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, count } },
        };
        if (!on)
        {
            tab.MouseEnter += (_, _) => tab.Background = HoverFill;
            tab.MouseLeave += (_, _) => tab.Background = Brushes.Transparent;
        }
        tab.MouseLeftButtonUp += (_, e) => { e.Handled = true; Show(group); };
        return tab;
    }

    UIElement Tile(Pattern p)
    {
        var on = p == selected();
        var picture = new Image
        {
            Source = Thumb(p), Width = ThumbW, Height = ThumbH, Stretch = Stretch.UniformToFill,
            // Pictures are never mirrored by the right-to-left layout.
            FlowDirection = FlowDirection.LeftToRight,
            Clip = new RectangleGeometry(new Rect(0, 0, ThumbW, ThumbH), 7, 7),
        };
        var name = new TextBlock
        {
            Text = p.Name, FontSize = 11.5, FlowDirection = FlowDirection.LeftToRight,
            Foreground = on ? Gold : Ink, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ThumbW + 4, Margin = new Thickness(0, 6, 0, 0),
        };
        var badge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Gold,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5),
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 },
            Child = new TextBlock
            {
                Text = "", FontFamily = Icons, FontSize = 11, Foreground = GoldInk,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            Width = CellW - 8, Height = CellH - 8, Padding = new Thickness(3), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10), BorderBrush = on ? Gold : Brushes.Transparent,
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.5),
            Child = new Grid { Children = { new StackPanel { Children = { picture, name } }, badge } },
        };

        void Grow(double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        tile.MouseEnter += (_, _) => { tile.Background = HoverFill; Grow(1.05); };
        tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; Grow(1); };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            choose(p);
            currentName.Text = p.Name;
            Show(shown);
        };
        return tile;
    }

    BitmapSource Thumb(Pattern p)
    {
        // Decoded at twice the shown size, sharp on high-DPI screens; once per design.
        if (!thumbs.TryGetValue(p.Id, out var image))
            thumbs[p.Id] = image = Patterns.Thumbnail(p, (int)(ThumbW * 2.2));
        return image;
    }

    // ---------- switches ----------

    /// <summary>A setting with an on/off switch; clicking anywhere on the row flips it.</summary>
    UIElement ToggleRow(string glyph, string text, Func<bool> get, Action<bool> set)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        var label = new TextBlock { Text = text, FontSize = 13.5, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
        var knob = new Ellipse
        {
            Width = 14, Height = 14, Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35 },
        };
        var track = new Border
        {
            Width = 38, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = knob,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } });
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var row = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid,
        };

        // On: a gold track with the knob at the far end (the left, in a right-to-left menu).
        void Refresh()
        {
            var on = get();
            track.Background = on ? Gold : Brushes.Transparent;
            track.BorderBrush = on ? Gold : Muted;
            knob.Fill = on ? GoldInk : Muted;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            icon.Foreground = on ? Gold : Muted;
        }
        toggleRefreshers.Add(Refresh);
        Refresh();

        row.MouseEnter += (_, _) => row.Background = HoverFill;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            set(!get());
            Refresh();
            // Rug of the day may just have laid a new rug.
            currentName.Text = selected().Name;
            Show(shown);
        };
        return row;
    }

    // ---------- actions ----------

    UIElement ActionRow(string glyph, string text, Action act, bool danger)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        var label = new TextBlock { Text = text, FontSize = 13.5, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
        var row = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = HoverFill;
            icon.Foreground = label.Foreground = danger ? Danger : Gold;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            icon.Foreground = Muted;
            label.Foreground = Ink;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
            act();
        };
        return row;
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
