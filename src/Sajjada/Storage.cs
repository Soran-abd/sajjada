using System.IO;
using System.Text.Json;

namespace Sajjada;

public class RugState
{
    // Where the rug's window lies, in physical screen pixels (the same on every screen,
    // whatever its scaling). Null until the rug is first moved or resized.
    public int? ScreenX { get; set; }
    public int? ScreenY { get; set; }

    // Older settings: the position in scaled units, read only when there is no pixel position yet.
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    // Height always follows from the width at 3:2, the shape of the pattern photos.
    public double Width { get; set; } = 420;
    public string Pattern { get; set; } = Patterns.FirstId;

    // "Rug of the day": a new design every day, and the day it was last changed (yyyy-MM-dd).
    public bool DailyRug { get; set; }
    public string? DailyDate { get; set; }
    // The day it was switched on (yyyy-MM-dd): that day shows the first rug of the first tab.
    public string? DailyStart { get; set; }

    // Desktop icon name -> where it was on the desktop before it was dropped on the rug, in
    // desktop pixels, so "bring all icons out" can put it back there.
    public Dictionary<string, int[]> Before { get; set; } = new();
}

/// <summary>
/// Only the rug's own settings: where it lies, how big it is, which pattern it shows and which
/// desktop icons lie under it. The app never touches any other file.
/// </summary>
public class Storage
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sajjada");

    static readonly string StateFile = Path.Combine(Root, "state.json");
    // A rug that was never moved has no position yet (NaN); plain JSON cannot hold NaN.
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public RugState State { get; private set; } = new();

    public Storage()
    {
        try
        {
            if (File.Exists(StateFile))
                State = JsonSerializer.Deserialize<RugState>(File.ReadAllText(StateFile), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { State = new(); }
        State.Pattern ??= Patterns.FirstId;
        State.Before = new((State.Before ?? new()).Where(b => b.Value is { Length: 2 }), StringComparer.OrdinalIgnoreCase);
        if (!double.IsFinite(State.Width)) State.Width = 420;
    }

    public void Save()
    {
        // Written to a new file beside it and then swapped in, so a power cut never leaves half a file.
        var temp = Path.Combine(Root, $"state.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Root);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, State, JsonOptions);
            File.Move(temp, StateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
