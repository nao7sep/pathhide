using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PathHide.Models;

public sealed class WindowPlacementState
{
    [JsonConverter(typeof(WindowPlacementsConverter))]
    public WindowPlacements WindowPlacements { get; set; } = new();
}

public sealed class WindowPlacements
{
    public WindowPlacement? Main { get; set; }
}

public sealed class WindowPlacement
{
    public WindowBounds? NormalBounds { get; set; }
    public string Mode { get; set; } = "normal";
}

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    // Legacy fields above are physical outer bounds. New saves also retain the
    // logical client size, matching Avalonia's Width/Height and ClientSize APIs.
    public double? ClientWidth { get; set; }
    public double? ClientHeight { get; set; }
}

public sealed class WindowPlacementsConverter : JsonConverter<WindowPlacements>
{
    public override bool HandleNull => true;

    public override WindowPlacements Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("main", out var main)
            || main.ValueKind != JsonValueKind.Object)
            return new WindowPlacements();
        var mode = main.TryGetProperty("mode", out var modeValue)
            && modeValue.ValueKind == JsonValueKind.String
            && modeValue.GetString() is "normal" or "maximized"
                ? modeValue.GetString()!
                : "normal";
        WindowBounds? bounds = null;
        if (main.TryGetProperty("normalBounds", out var normal))
        {
            try
            {
                bounds = normal.Deserialize<WindowBounds>(options);
            }
            catch (JsonException)
            {
                // Disposable geometry may be malformed independently of a valid mode.
                bounds = null;
            }
        }
        return new WindowPlacements { Main = new WindowPlacement { NormalBounds = bounds, Mode = mode } };
    }

    public override void Write(Utf8JsonWriter writer, WindowPlacements value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("main");
        JsonSerializer.Serialize(writer, value.Main, options);
        writer.WriteEndObject();
    }

}

public sealed record DisplayWorkArea(int X, int Y, int Width, int Height, double Scaling);

public static class WindowPlacementPolicy
{
    public static WindowPlacement Resolve(
        WindowPlacement? saved,
        IEnumerable<DisplayWorkArea> displays)
    {
        var bounds = saved?.NormalBounds;
        return new WindowPlacement
        {
            Mode = saved?.Mode is "normal" or "maximized" ? saved.Mode : "normal",
            NormalBounds = bounds is { Width: > 0, Height: > 0 }
                && FindDisplay(bounds, displays) is not null ? bounds : null,
        };
    }

    // Avalonia has no off-screen geometry restoration facility. Keep any
    // intersecting placement; the window boundary fits it to this work area.
    public static DisplayWorkArea? FindDisplay(
        WindowBounds bounds,
        IEnumerable<DisplayWorkArea> displays) => displays.FirstOrDefault(display =>
            display.Width > 0 && display.Height > 0
            && double.IsFinite(display.Scaling) && display.Scaling > 0
            && (long)bounds.X + bounds.Width > display.X
            && (long)bounds.Y + bounds.Height > display.Y
            && bounds.X < (long)display.X + display.Width
            && bounds.Y < (long)display.Y + display.Height);
}
