using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Sajjada;

/// <summary>
/// The shape of the rug at one moment: where a corner is pulled to, how far the parts
/// away from the hand trail behind while carried, and how big the lump of hidden things is.
/// </summary>
public struct RugPose
{
    public Point PeelCorner;   // corner being lifted
    public Point PeelTip;      // where that corner is now
    public Vector Sway;        // how far the far edge trails behind the hand, px
    public Point SwayAnchor;   // where the rug is held
    public double LumpHeight;  // px
}

/// <summary>
/// Page-curl fold: the corner side of a fold line rolls around a cylinder of radius R
/// and lies flipped over the rest of the rug, so the corner lands exactly on the tip.
/// </summary>
public readonly struct Fold
{
    public readonly Point Corner;
    public readonly Vector Dir;   // from the corner toward the tip
    public readonly double Line;  // fold line, measured from the corner along Dir
    public readonly double R;
    public readonly bool Active;

    public Fold(Point corner, Point tip, double radius)
    {
        Corner = corner;
        var d = tip - corner;
        var len = d.Length;
        // A barely lifted corner has a tighter roll. With the full radius a corner a pixel
        // off the floor would still curl a wide strip, in whatever direction that pixel points.
        R = Math.Min(radius, len / Math.PI);
        Active = len > 0.3;
        Dir = Active ? d / len : new Vector(1, 0);
        Line = (len + Math.PI * R) / 2;
    }

    /// <summary>Distance past the fold line toward the corner; positive means lifted.</summary>
    public double Lift(Point p) => Active ? Line - Vector.Multiply(p - Corner, Dir) : -1e9;

    /// <summary>Floor that nothing covers any more, not even the rolled part.</summary>
    public bool Uncovers(Point p) => Lift(p) > R;

    /// <summary>Whether p, on screen, lies on the lifted part (its roll or its flipped back).</summary>
    public bool OnFlap(Point p, Rect body)
    {
        if (!Active) return false;
        var u = Vector.Multiply(p - Corner, Dir);
        if (u < Line - R) return false;
        if (u <= Line) return body.Contains(p);
        // Unfold the flat flipped part back to where that piece of rug came from.
        return body.Contains(p - Dir * (2 * (u - Line) + Math.PI * R));
    }

    public (Point, double height) Map(Point p)
    {
        var t = Lift(p);
        if (t <= 0) return (p, 0);
        double along, h;
        if (t < Math.PI * R)
        {
            var a = t / R;
            along = -R * Math.Sin(a);
            h = R * (1 - Math.Cos(a));
        }
        else
        {
            along = t - Math.PI * R;
            h = 2 * R;
        }
        // Positive "along" moves away from the corner, over the rest of the rug.
        var u = Line + along;
        var u0 = Line - t;
        return (p + Dir * (u - u0), h);
    }

    /// <summary>
    /// The way the lifted surface faces at lift t (t ≥ 0), exactly: around the roll it turns
    /// from facing the viewer (-z) to facing away (+z), tilting toward Dir on the way.
    /// </summary>
    public Vector3D Normal(double t)
    {
        var a = R > 0 ? Math.Clamp(t / R, 0, Math.PI) : Math.PI;
        return new Vector3D(Dir.X * Math.Sin(a), Dir.Y * Math.Sin(a), -Math.Cos(a));
    }
}

/// <summary>
/// Builds the rug and fringe meshes, split into the flat part and the lifted flap.
///
/// The rug is a grid of small squares. Squares crossed by the fold are cut exactly along the
/// fold line, and the part that rolls around the curl is cut further into thin strips, so the
/// edge between the flat and the lifted rug is straight and the picture is not stretched where
/// it bends. Points made by those cuts are shared by the pieces on both sides, so the surface
/// has no cracks. Around the roll the surface's facing is taken from the cylinder itself.
/// </summary>
public class RugGeometry
{
    const int NX = 64, NY = 42;
    // Strips across the roll: about 7 px each on the largest roll.
    const int RollStrips = 6;
    public const double FringeLength = 15;

    // Light comes low from the upper left, like a window: bumps and folds read clearly.
    public static readonly Vector3D LightDir = new(0.6, 0.7, 0.62);

    public readonly MeshGeometry3D BaseMesh = new(), FlapMesh = new();
    public readonly MeshGeometry3D BaseFringe = new(), FlapFringe = new();

    static readonly Point3DCollection NoPoints = new();
    static RugGeometry() => NoPoints.Freeze();

    const int GridCount = (NX + 1) * (NY + 1);
    readonly Point3D[] gridPos = new Point3D[GridCount];
    readonly Vector3D[] gridNormal = new Vector3D[GridCount];
    readonly double[] lift = new double[GridCount];

    // This frame's vertices: the grid first, then the points made by cuts.
    readonly List<Point3D> positions = new(GridCount * 2);
    readonly List<Vector3D> normals = new(GridCount * 2);
    readonly List<Point> uvs = new(GridCount * 2);
    readonly List<int> baseIdx = new(NX * NY * 6), flapIdx = new();
    readonly Dictionary<long, int> cutPoints = new();
    double[] levels = Array.Empty<double>();

    Rect body;
    Fold fold;
    RugPose pose;
    Point center;
    double reach;

    /// <summary>Everything drawn, rug and fringes, as seen from above: a lifted flap can reach past the rug.</summary>
    public Rect Extent { get; private set; }

    static int Idx(int i, int j) => j * (NX + 1) + i;

    Point GridPoint(int k) => new(body.X + body.Width * (k % (NX + 1)) / NX, body.Y + body.Height * (k / (NX + 1)) / NY);

    /// <summary>What it is made of: a leather pad has no fringes, lies perfectly flat and bends in a wider, stiffer curl.</summary>
    public MadeOf Kind = MadeOf.Wool;

    public static double CurlRadius(Rect body) => Math.Clamp(Math.Min(body.Width, body.Height) * 0.035, 6, 13);

    public Fold FoldFor(Rect body, RugPose pose) =>
        new(pose.PeelCorner, pose.PeelTip, CurlRadius(body) * (Kind == MadeOf.Leather ? 1.7 : 1));

    /// <summary>Where a point of the flat rug is drawn: lifted by the fold, swelling, trailing while carried.</summary>
    Point3D Surface(Point p, double t)
    {
        // Bumps flatten out near the fold so the lifted part meets the floor cleanly.
        var flat = t >= 0 ? 0 : SmoothStep(0, 3 * fold.R, -t);
        // Swells belong to the rug, not to the screen: measured from the rug's corner, so they
        // stay put on it while it is carried (offset to look as they always have).
        var onRug = new Point(p.X - body.X + 48, p.Y - body.Y + 48);
        // Leather lies perfectly flat: only its thickness, none of the swells of wool.
        var h = flat * (Lump(p, center, pose.LumpHeight) + (Kind == MadeOf.Leather ? 1.0 : Settle(onRug)));
        var (q, fh) = fold.Map(p);
        // Held at one point, the rest hangs back with its weight and lifts a little in the air.
        if (pose.Sway.LengthSquared > 0)
        {
            var hang = Math.Pow(Math.Min((p - pose.SwayAnchor).Length / reach, 1), 1.2);
            q += pose.Sway * hang;
            // Stiffer leather lifts less where it hangs back.
            h += (Kind == MadeOf.Leather ? 0.3 : 0.5) * pose.Sway.Length * hang;
        }
        // Screen y grows downward; the viewer looks along +z, so height is -z.
        return new Point3D(q.X, q.Y, -(h + fh));
    }

    public void Update(Rect bodyRect, RugPose rugPose)
    {
        body = bodyRect;
        pose = rugPose;
        fold = FoldFor(body, pose);
        center = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);
        reach = Math.Max(1, new[] { body.TopLeft, body.TopRight, body.BottomRight, body.BottomLeft }.Max(c => (c - pose.SwayAnchor).Length));

        // The lines the pieces are cut along: the fold line itself, then strips across the roll.
        if (fold.Active && fold.R > 0)
        {
            if (levels.Length != RollStrips + 1) levels = new double[RollStrips + 1];
            for (var k = 0; k <= RollStrips; k++) levels[k] = Math.PI * fold.R * k / RollStrips;
        }
        else levels = Array.Empty<double>();

        for (var k = 0; k < GridCount; k++)
        {
            var p = GridPoint(k);
            lift[k] = fold.Lift(p);
            gridPos[k] = Surface(p, lift[k]);
        }
        GridNormals();

        positions.Clear(); normals.Clear(); uvs.Clear();
        baseIdx.Clear(); flapIdx.Clear(); cutPoints.Clear();
        positions.AddRange(gridPos);
        normals.AddRange(gridNormal);
        for (var k = 0; k < GridCount; k++) uvs.Add(new Point((double)(k % (NX + 1)) / NX, (double)(k / (NX + 1)) / NY));

        for (var j = 0; j < NY; j++)
            for (var i = 0; i < NX; i++)
            {
                int a = Idx(i, j), b = Idx(i + 1, j), c = Idx(i, j + 1), d = Idx(i + 1, j + 1);
                Triangle(a, c, b);
                Triangle(b, c, d);
            }

        var p3 = new Point3DCollection(positions); p3.Freeze();
        var n3 = new Vector3DCollection(normals); n3.Freeze();
        var tx = new PointCollection(uvs); tx.Freeze();
        Set(BaseMesh, p3, n3, tx, Frozen(baseIdx));
        Set(FlapMesh, p3, n3, tx, Frozen(flapIdx));

        var fringeExtent = UpdateFringes();
        double minX = fringeExtent.Left, minY = fringeExtent.Top, maxX = fringeExtent.Right, maxY = fringeExtent.Bottom;
        foreach (var q in positions)
        {
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        Extent = new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    static Int32Collection Frozen(List<int> list)
    {
        var c = new Int32Collection(list);
        c.Freeze();
        return c;
    }

    /// <summary>
    /// Facing of each grid point: from its neighbours on the flat and swelling rug, and exactly
    /// from the roll where the rug is lifted (neighbours a square apart would smear the bend).
    /// </summary>
    void GridNormals()
    {
        for (var j = 0; j <= NY; j++)
            for (var i = 0; i <= NX; i++)
            {
                var k = Idx(i, j);
                if (lift[k] >= 0 && fold.Active)
                {
                    gridNormal[k] = fold.Normal(lift[k]);
                    continue;
                }
                var du = gridPos[Idx(Math.Min(i + 1, NX), j)] - gridPos[Idx(Math.Max(i - 1, 0), j)];
                var dv = gridPos[Idx(i, Math.Min(j + 1, NY))] - gridPos[Idx(i, Math.Max(j - 1, 0))];
                var n = Vector3D.CrossProduct(dv, du);
                // Where the fold squeezes neighbours together the cross product can vanish;
                // a NaN normal makes WPF drop the whole mesh for that frame.
                if (n.Length < 1e-9 || double.IsNaN(n.Length)) n = new Vector3D(0, 0, -1);
                else n.Normalize();
                gridNormal[k] = n;
            }
    }

    // ---------- cutting a grid triangle along the fold ----------

    /// <summary>A corner of a piece: a grid point, or a point a cut made on the grid edge A-B.</summary>
    struct Corner
    {
        public int Index;   // vertex in this frame's list
        public double T;    // lift
        public int A, B;    // grid edge it lies on (A == B for a grid point)
    }

    void Triangle(int a, int b, int c)
    {
        double ta = lift[a], tb = lift[b], tc = lift[c];
        double lo = Math.Min(ta, Math.Min(tb, tc)), hi = Math.Max(ta, Math.Max(tb, tc));

        // Wholly flat, or wholly on one strip of the lift: kept whole.
        var cuts = false;
        foreach (var level in levels) if (level > lo && level < hi) { cuts = true; break; }
        if (!cuts)
        {
            var target = lo >= 0 && fold.Active ? flapIdx : baseIdx;
            target.Add(a); target.Add(b); target.Add(c);
            return;
        }

        // Cut into one piece per strip it reaches: below the fold line (flat rug), each strip of
        // the roll, and past the roll (the flipped-over part).
        var tri = new[] { Grid(a), Grid(b), Grid(c) };
        for (var s = -1; s < levels.Length; s++)
        {
            var from = s < 0 ? double.NegativeInfinity : levels[s];
            var to = s + 1 < levels.Length ? levels[s + 1] : double.PositiveInfinity;
            if (to <= lo || from >= hi) continue;
            var piece = ClipAbove(tri, from, s);
            piece = ClipBelow(piece, to, s + 1);
            if (piece.Count < 3) continue;
            var target = from >= 0 ? flapIdx : baseIdx;
            for (var k = 1; k + 1 < piece.Count; k++)
            {
                target.Add(piece[0].Index); target.Add(piece[k].Index); target.Add(piece[k + 1].Index);
            }
        }
    }

    Corner Grid(int k) => new() { Index = k, T = lift[k], A = k, B = k };

    // Keeps the part of the polygon with lift ≥ level (none when level is -∞).
    List<Corner> ClipAbove(IReadOnlyList<Corner> poly, double level, int levelIndex)
    {
        if (double.IsNegativeInfinity(level)) return new List<Corner>(poly);
        return Clip(poly, level, levelIndex, keepAbove: true);
    }

    List<Corner> ClipBelow(IReadOnlyList<Corner> poly, double level, int levelIndex)
    {
        if (double.IsPositiveInfinity(level)) return poly as List<Corner> ?? new List<Corner>(poly);
        return Clip(poly, level, levelIndex, keepAbove: false);
    }

    List<Corner> Clip(IReadOnlyList<Corner> poly, double level, int levelIndex, bool keepAbove)
    {
        var result = new List<Corner>(poly.Count + 2);
        bool In(Corner v) => keepAbove ? v.T >= level : v.T <= level;
        for (var i = 0; i < poly.Count; i++)
        {
            Corner v = poly[i], w = poly[(i + 1) % poly.Count];
            if (In(v)) result.Add(v);
            if (In(v) != In(w) && v.T != level && w.T != level) result.Add(CutPoint(v, w, level, levelIndex));
        }
        return result;
    }

    /// <summary>
    /// The point where the cut at this level crosses the edge between two corners. Every such
    /// edge lies on a grid edge (cut lines are parallel and never cross each other), and the
    /// point is made once per grid edge and level, so neighbouring pieces share it exactly.
    /// </summary>
    Corner CutPoint(Corner v, Corner w, double level, int levelIndex)
    {
        int A, B;
        if (v.A == v.B && w.A == w.B) (A, B) = (v.A, w.A);
        else if (v.A != v.B) (A, B) = (v.A, v.B);
        else (A, B) = (w.A, w.B);
        if (A > B) (A, B) = (B, A);

        var key = ((long)A * GridCount + B) * 64 + levelIndex;
        if (!cutPoints.TryGetValue(key, out var index))
        {
            var s = (level - lift[A]) / (lift[B] - lift[A]);
            var pa = GridPoint(A);
            var p = pa + (GridPoint(B) - pa) * s;
            index = positions.Count;
            positions.Add(Surface(p, level));
            normals.Add(level >= 0 ? fold.Normal(level) : new Vector3D(0, 0, -1));
            uvs.Add(new Point((p.X - body.X) / body.Width, (p.Y - body.Y) / body.Height));
            cutPoints[key] = index;
        }
        return new Corner { Index = index, T = level, A = A, B = B };
    }

    static void Set(MeshGeometry3D mesh, Point3DCollection p, Vector3DCollection n, PointCollection t, Int32Collection idx)
    {
        // With no indices WPF reads the positions as a triangle list and draws stray slivers.
        if (idx.Count == 0) p = NoPoints;
        mesh.Positions = p;
        mesh.Normals = n;
        mesh.TextureCoordinates = t;
        mesh.TriangleIndices = idx;
    }

    // ---------- fringes ----------

    /// <summary>
    /// Fringes hang off the two short sides and follow the edge when it curls. Along each side
    /// a point is added wherever a cut line crosses it, so fringes too split exactly at the fold.
    /// Returns how far the fringe tips reach.
    /// </summary>
    Rect UpdateFringes()
    {
        if (Kind == MadeOf.Leather)
        {
            var none = new Int32Collection(); none.Freeze();
            var nn = new Vector3DCollection(); nn.Freeze();
            var nt = new PointCollection(); nt.Freeze();
            Set(BaseFringe, NoPoints, nn, nt, none);
            Set(FlapFringe, NoPoints, nn, nt, none);
            return Rect.Empty;
        }
        var p = new Point3DCollection();
        var n = new Vector3DCollection();
        var t = new PointCollection();
        var lifts = new List<double>();
        var sideStart = new int[3];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        for (var side = 0; side < 2; side++)
        {
            sideStart[side] = lifts.Count;
            int edge = side == 0 ? 0 : NX, inner = side == 0 ? 1 : NX - 1;
            var inwardStep = new Vector((side == 0 ? 1 : -1) * body.Width / NX, 0);
            for (var j = 0; j <= NY; j++)
            {
                AddFringePoint(j, Idx(edge, j), Idx(inner, j));
                if (j == NY) continue;
                // Cuts crossing the edge between this grid point and the next, in order along it.
                double t0 = lift[Idx(edge, j)], t1 = lift[Idx(edge, j + 1)];
                var crossings = levels.Where(l => l > Math.Min(t0, t1) && l < Math.Max(t0, t1))
                                      .Select(l => (l - t0) / (t1 - t0)).OrderBy(s => s);
                foreach (var s in crossings) AddFringeCut(j + s, inwardStep);
            }
            void AddFringePoint(int j, int k, int innerK)
            {
                Add(gridPos[k], gridPos[innerK], gridNormal[k], j, lift[k]);
            }
            void AddFringeCut(double jf, Vector step)
            {
                var at = new Point(side == 0 ? body.Left : body.Right, body.Y + body.Height * jf / NY);
                var tl = fold.Lift(at);
                var inside = at + step;
                Add(Surface(at, tl), Surface(inside, fold.Lift(inside)), tl >= 0 && fold.Active ? fold.Normal(tl) : new Vector3D(0, 0, -1), jf, tl);
            }
            void Add(Point3D e, Point3D innerPoint, Vector3D normal, double jf, double tl)
            {
                var outward = e - innerPoint;
                if (outward.Length < 1e-9) outward = new Vector3D(side == 0 ? -1 : 1, 0, 0);
                else outward.Normalize();
                // A little droop: fringes on a flat rug lie on the floor, slightly fanned.
                var wobble = Math.Sin(jf * 2.3) * 0.08;
                var tip = e + (outward + new Vector3D(0, wobble, 0)) * FringeLength;
                p.Add(e); p.Add(tip);
                n.Add(normal); n.Add(normal);
                t.Add(new Point(0, jf / NY)); t.Add(new Point(1, jf / NY));
                lifts.Add(tl);
                minX = Math.Min(minX, Math.Min(e.X, tip.X)); maxX = Math.Max(maxX, Math.Max(e.X, tip.X));
                minY = Math.Min(minY, Math.Min(e.Y, tip.Y)); maxY = Math.Max(maxY, Math.Max(e.Y, tip.Y));
            }
        }
        sideStart[2] = lifts.Count;
        p.Freeze(); n.Freeze(); t.Freeze();

        var baseList = new Int32Collection();
        var flapList = new Int32Collection();
        for (var side = 0; side < 2; side++)
            for (var r = sideStart[side]; r + 1 < sideStart[side + 1]; r++)
            {
                int a = 2 * r, b = a + 1, c = a + 2, d = a + 3;
                // Each strip between two points goes wholly to the lifted or the flat part, by its middle.
                var target = fold.Active && (lifts[r] + lifts[r + 1]) / 2 > 0 ? flapList : baseList;
                // Winding differs per side so both strips face the viewer.
                if (side == 0) { target.Add(a); target.Add(b); target.Add(c); target.Add(c); target.Add(b); target.Add(d); }
                else { target.Add(a); target.Add(c); target.Add(b); target.Add(b); target.Add(c); target.Add(d); }
            }
        baseList.Freeze();
        flapList.Freeze();
        Set(BaseFringe, p, n, t, baseList);
        Set(FlapFringe, p, n, t, flapList);
        return lifts.Count == 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    Point[] Corners => new[] { body.TopLeft, body.TopRight, body.BottomRight, body.BottomLeft };

    /// <summary>The part of a rectangle where f is not negative.</summary>
    static List<Point> Clip(Rect r, Func<Point, double> f)
    {
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        var result = new List<Point>();
        for (var i = 0; i < 4; i++)
        {
            Point a = corners[i], b = corners[(i + 1) % 4];
            double fa = f(a), fb = f(b);
            if (fa >= 0) result.Add(a);
            if ((fa >= 0) != (fb >= 0)) result.Add(a + (b - a) * (fa / (fa - fb)));
        }
        return result;
    }

    /// <summary>Outline of the lifted flap's shadow on whatever is below it.</summary>
    public Geometry? FlapShadow(RugPose pose)
    {
        var f = FoldFor(body, pose);
        if (!f.Active) return null;
        var clipped = Clip(body, f.Lift);
        if (clipped.Count < 3) return null;

        var light = new Vector(LightDir.X, LightDir.Y) / LightDir.Z;
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        var first = true;
        for (var i = 0; i < clipped.Count; i++)
        {
            Point a = clipped[i], b = clipped[(i + 1) % clipped.Count];
            for (var s = 0; s < 24; s++)
            {
                var (q, h) = f.Map(a + (b - a) * (s / 24.0));
                var shadowPoint = q + light * h * 0.5;
                if (first) { figure.StartPoint = shadowPoint; first = false; }
                else figure.Segments.Add(new LineSegment(shadowPoint, false));
            }
        }
        var geometry = new PathGeometry(new[] { figure }) { FillRule = FillRule.Nonzero };
        geometry.Freeze();
        return geometry;
    }

    static double Lump(Point p, Point center, double height)
    {
        if (height <= 0) return 0;
        var dx = (p.X - center.X) / (height * 2.5 + 34);
        var dy = (p.Y - center.Y) / (height * 1.6 + 24);
        return height * Math.Exp(-(dx * dx + dy * dy));
    }

    // A real rug never lies perfectly flat; these soft swells catch the light.
    static double Settle(Point p) =>
        0.8 * Math.Sin(p.X / 83 + 0.7) * Math.Sin(p.Y / 61 + 1.3) + 0.5 * Math.Sin((p.X + p.Y) / 127) + 1.3;

    static double SmoothStep(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
