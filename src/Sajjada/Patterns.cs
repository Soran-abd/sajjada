using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sajjada;

/// <summary>What a design is made of, which changes how it lies, bends and is drawn.</summary>
public enum MadeOf
{
    /// <summary>A wool rug: fringes on the short sides, soft swells, a tight curl when a corner lifts.</summary>
    Wool,
    /// <summary>A leather desk pad: no fringes, perfectly flat, stiffer, rounded corners, suede underneath.</summary>
    Leather,
}

/// <param name="Id">the texture's file name in Assets\Patterns</param>
/// <param name="Name">what the menu shows, in English whatever the language</param>
/// <param name="Fringe">wool colour of the fringes: the rug's own light wool, blended with undyed wool</param>
/// <param name="Corner">leather: radius of the rounded corners as a share of the width (tools\prepare_leather.ps1)</param>
public record Pattern(string Id, string Name, Color Fringe, MadeOf Kind = MadeOf.Wool, double Corner = 0)
{
    public bool IsLeather => Kind == MadeOf.Leather;
}

/// <summary>A heading in the menu's pattern gallery and the designs under it.</summary>
public record PatternGroup(string Title, Pattern[] Patterns);

/// <summary>Rug designs, their textures and the colors derived from them.</summary>
static class Patterns
{
    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    static Pattern Leather(string id, string name, double corner) => new(id, name, default, MadeOf.Leather, corner);

    public static readonly PatternGroup[] Groups =
    {
        new("العراق", new Pattern[]
        {
            new("ishtar", "Ishtar Gate", C(0xF5, 0xDF, 0xB8)),
            new("erbil", "Erbil Citadel", C(0xF1, 0xE2, 0xBD)),
            new("kirkuk", "Kirkuk Citadel", C(0xF2, 0xE3, 0xBD)),
            new("baghdad", "Tahrir Square", C(0xF2, 0xE8, 0xCB)),
            new("hadba", "Al-Hadba", C(0xF1, 0xD9, 0xB5)),
            new("samarra", "Samarra", C(0xF1, 0xE0, 0xBB)),
            new("ahwar", "The Marshes", C(0xF1, 0xE4, 0xC1)),
            new("rafidain", "Dijla wal Furat", C(0xE6, 0xD0, 0xB1)),
        }),
        new("كلاسيكي", new Pattern[]
        {
            new("kilim", "Anatolian Kilim", C(0xDD, 0xCF, 0xB6)),
            new("kashi", "Kashi Star", C(0xF1, 0xE9, 0xD5)),
            new("kurdi", "Kurdish Kilim", C(0xF0, 0xE1, 0xBE)),
            new("kazak", "Kazak", C(0xEC, 0xD0, 0xA8)),
            new("gabbeh", "Gabbeh", C(0xE6, 0xD5, 0xB8)),
            new("warda", "Roses", C(0xF0, 0xE1, 0xCC)),
            // A plain red rug: ivory fringes, not the red of the rug itself.
            new("ahmar", "Persian Red", C(0xE8, 0xDC, 0xC4)),
        }),
        new("صوف وفرو", new Pattern[]
        {
            new("beniourain", "Beni Ourain", C(0xEE, 0xE1, 0xCC)),
            new("azilal", "Azilal", C(0xF0, 0xE2, 0xCD)),
            new("sheep", "Natural Wool", C(0xEB, 0xDE, 0xC8)),
            new("flock", "Flock", C(0xE6, 0xDA, 0xC7)),
            new("cowhide", "Cowhide", C(0xE9, 0xDF, 0xCF)),
        }),
        new("عصري", new Pattern[]
        {
            new("pebbles", "Pebbles", C(0xE5, 0xD5, 0xBD)),
            new("bauhaus", "Bauhaus", C(0xDD, 0xCE, 0xBA)),
            new("brushstroke", "Brushstroke", C(0xE7, 0xDB, 0xC7)),
            new("marble", "Marble", C(0xE2, 0xD6, 0xC4)),
            new("hadi", "Modern Calm", C(0xE2, 0xCE, 0xB3)),
            new("bloom", "Blue Bloom", C(0xD7, 0xDB, 0xDC)),
            new("sunrise", "Sunrise", C(0xF0, 0xD9, 0xBE)),
            new("pier", "Lake Pier", C(0xE5, 0xCE, 0xC1)),
            new("contours", "Contours", C(0xDF, 0xCF, 0xB6)),
        }),
        new("جلد مكتبي", new Pattern[]
        {
            Leather("cognac", "Cognac", 0.027),
            Leather("executive", "Executive Green", 0.027),
            Leather("arabesque", "Midnight Arabesque", 0.027),
        }),
        new("مكتب المصدر", new Pattern[]
        {
            // A black rug: dark charcoal fringes keep it sleek.
            new("almasdar", "Al-Masdar", C(0x3A, 0x36, 0x32)),
        }),
    };

    public static readonly Pattern[] All = Groups.SelectMany(g => g.Patterns).ToArray();

    /// <summary>The rug anyone sees first, and the stand-in for a design that has since been removed.</summary>
    public const string FirstId = "pebbles";

    public static Pattern Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == FirstId);

    // Every rug in the menu's own order, tab after tab, so the rug of the day changes every day and
    // the same design comes back only after all the others have had their day. Rugs only: a desk
    // pad is chosen on purpose, it does not turn up by chance.
    static readonly Pattern[] DailyOrder = All.Where(p => !p.IsLeather).ToArray();

    /// <summary>The rug for a day: the first rug of the first tab on the day the option was switched on, then the next one each day.</summary>
    public static Pattern OfTheDay(DateTime day, DateTime start)
    {
        var n = (int)(day.Date - start.Date).TotalDays;
        return DailyOrder[((n % DailyOrder.Length) + DailyOrder.Length) % DailyOrder.Length];
    }

    public static PatternGroup GroupOf(Pattern p) => Groups.First(g => g.Patterns.Contains(p));

    /// <summary>A small copy of a design for the menu, decoded at the size it is shown.</summary>
    public static BitmapSource Thumbnail(Pattern p, int pixelWidth) => Load(p, pixelWidth);

    public static BitmapSource Texture(Pattern p) => Load(p, 0);

    static BitmapSource Load(Pattern p, int pixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/Patterns/{p.Id}.jpg");
        if (pixelWidth > 0) image.DecodePixelWidth = pixelWidth;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return p.IsLeather ? RoundCorners(image, p.Corner) : image;
    }

    /// <summary>
    /// The pad was photographed on a light background: outside its rounded corners that
    /// background is made see-through, a pixel and a half inside the curve (at full size) so none
    /// of it is left blended into the edge.
    /// </summary>
    static BitmapSource RoundCorners(BitmapSource image, double cornerShare)
    {
        var src = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var inset = 1.5 * w / 1440;
        var r = cornerShare * w;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = Math.Clamp(0.5 - RoundedRectDistance(x + 0.5 - w / 2.0, y + 0.5 - h / 2.0, w / 2.0 - inset, h / 2.0 - inset, r), 0, 1);
                if (a >= 1) continue;
                var i = (y * w + x) * 4;
                // Bgra32 keeps colour and coverage apart, so only the coverage changes.
                px[i + 3] = (byte)Math.Round(px[i + 3] * a);
            }
        var cut = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        cut.Freeze();
        return cut;
    }

    /// <summary>Signed distance from a point to a rounded rectangle centred on the origin; negative inside.</summary>
    public static double RoundedRectDistance(double x, double y, double halfW, double halfH, double r)
    {
        var qx = Math.Abs(x) - (halfW - r);
        var qy = Math.Abs(y) - (halfH - r);
        var ox = Math.Max(qx, 0);
        var oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>
    /// The underside of a desk pad: plain suede in a light tan with a faint nap, cut to the same
    /// rounded shape as the leather (its coverage taken from the leather's own picture).
    /// </summary>
    public static BitmapSource SuedeBack(BitmapSource leather)
    {
        var src = new FormatConvertedBitmap(leather, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var rnd = new Random(5);
        for (var i = 0; i < w * h; i++)
        {
            var nap = 0.94 + rnd.NextDouble() * 0.08;
            px[i * 4] = (byte)(0x9C * nap);
            px[i * 4 + 1] = (byte)(0xB5 * nap);
            px[i * 4 + 2] = (byte)(0xC9 * nap);
        }
        var back = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        back.Freeze();
        return back;
    }


    /// <summary>
    /// Knotted wool strands along one short side. x runs from the rug edge to the tip;
    /// three texels per pixel of rug height keep each strand solid instead of blurring into grey.
    /// </summary>
    public static BitmapSource FringeTexture(Color wool, double rugHeight)
    {
        const int w = 60, spacing = 9;
        var h = (int)(rugHeight * 3);
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (var y = 3; y < h; y += spacing)
        {
            var length = (int)(w * (0.84 + rnd.NextDouble() * 0.16));
            var shade = 0.88 + rnd.NextDouble() * 0.14;
            var sway = (rnd.NextDouble() - 0.5) * 0.08;
            for (var x = 0; x < length; x++)
            {
                // Tied knot near the rug, thinner toward the tip.
                var knot = x < 9 ? 0.84 : 1.0;
                var half = x < 9 ? 3.6 : 2.6;
                var cy = y + (int)Math.Round(sway * x);
                for (var dy = -4; dy <= 4; dy++)
                {
                    var yy = cy + dy;
                    if (yy < 0 || yy >= h) continue;
                    var cover = Math.Clamp(half - Math.Abs(dy) + 0.5, 0, 1);
                    if (cover <= 0) continue;
                    var tipFade = x > length - 6 ? (length - x) / 6.0 : 1;
                    var a = (byte)(255 * cover * tipFade);
                    var i = (yy * w + x) * 4;
                    if (a <= px[i + 3]) continue;
                    // Rounder strands: a little darker at their sides.
                    var s = shade * knot * (1 - Math.Abs(dy) * 0.05);
                    // Premultiplied BGRA.
                    px[i] = (byte)(wool.B * s * a / 255);
                    px[i + 1] = (byte)(wool.G * s * a / 255);
                    px[i + 2] = (byte)(wool.R * s * a / 255);
                    px[i + 3] = a;
                }
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Soft shadow of a w×h rectangle with blur radius r, computed directly. A BlurEffect leaves
    /// bright specks in the nearly transparent pixels of a layered window.
    /// </summary>
    /// <param name="corner">radius of rounded corners (a leather pad's), 0 for a rug's square ones</param>
    public static BitmapSource SoftShadow(double width, double height, double r, double corner = 0)
    {
        var pad = (int)Math.Ceiling(r * 2);
        int w = (int)Math.Ceiling(width) + 2 * pad, h = (int)Math.Ceiling(height) + 2 * pad;
        var s = r / 2;
        double Cover(double v, double a, double b) => 0.5 * (Erf((v - a) / s) - Erf((v - b) / s));
        var fx = new double[w];
        var fy = new double[h];
        for (var x = 0; x < w; x++) fx[x] = Cover(x + 0.5, pad, pad + width);
        for (var y = 0; y < h; y++) fy[y] = Cover(y + 0.5, pad, pad + height);

        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = fx[x] * fy[y];
                if (corner > 0)
                {
                    // Rounded: blurred by the distance to the outline, the same blur along the straight edges.
                    var d = RoundedRectDistance(x + 0.5 - w / 2.0, y + 0.5 - h / 2.0, width / 2, height / 2, corner);
                    a = 0.5 * (1 - Erf(d / s));
                }
                px[(y * w + x) * 4 + 3] = (byte)Math.Round(255 * a);
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // Abramowitz-Stegun approximation, plenty for a shadow.
    static double Erf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x));
        var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }
}
