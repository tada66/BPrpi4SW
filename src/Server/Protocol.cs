using System;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// JSON message envelope used for all WebSocket text frames.
/// </summary>
public class Message
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";      // "request", "response", "event", "error"
    
    [JsonPropertyName("id")]
    public string? Id { get; set; }             // UUID for request↔response correlation, null for events
    
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";     // "camera", "mount", "system"
    
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";    // e.g. "camera.list", "mount.stop"
    
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    // ── Factory methods ──

    public static Message Request(string action, object? payload = null) => new()
    {
        Type = "request",
        Id = Guid.NewGuid().ToString(),
        Topic = action.Split('.')[0],
        Action = action,
        Payload = payload != null ? JsonSerializer.SerializeToElement(payload) : null
    };

    public static Message Response(string id, string action, object? payload = null) => new()
    {
        Type = "response",
        Id = id,
        Topic = action.Split('.')[0],
        Action = action,
        Payload = payload != null ? JsonSerializer.SerializeToElement(payload) : null
    };

    public static Message Event(string action, object? payload = null) => new()
    {
        Type = "event",
        Id = null,
        Topic = action.Split('.')[0],
        Action = action,
        Payload = payload != null ? JsonSerializer.SerializeToElement(payload) : null
    };

    public static Message Error(string? id, string action, string message) => new()
    {
        Type = "error",
        Id = id,
        Topic = action.Split('.')[0],
        Action = action,
        Payload = JsonSerializer.SerializeToElement(new { error = message })
    };

    // ── Serialization ──

    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public string Serialize() => JsonSerializer.Serialize(this, _opts);

    public static Message? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Message>(json, _opts); }
        catch { return null; }
    }

    /// <summary>
    /// Deserialize the payload to a specific type.
    /// </summary>
    public T? GetPayload<T>()
    {
        if (Payload == null) return default;
        return JsonSerializer.Deserialize<T>(Payload.Value.GetRawText(), _opts);
    }
}

#region Camera Payloads

public record CameraConnectPayload(
    [property: JsonPropertyName("camera")] string Camera
);

public record CameraInfoPayload(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("battery")] string Battery,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("capabilities")] CameraCapabilities? Capabilities
);

public record CameraCapabilities(
    [property: JsonPropertyName("liveView")] bool LiveView,
    [property: JsonPropertyName("imageCapture")] bool ImageCapture,
    [property: JsonPropertyName("triggerCapture")] bool TriggerCapture,
    [property: JsonPropertyName("configuration")] bool Configuration
);

public record CameraSettingsPayload(
    [property: JsonPropertyName("iso")] PropertyInfo Iso,
    [property: JsonPropertyName("shutterSpeed")] PropertyInfo ShutterSpeed,
    [property: JsonPropertyName("aperture")] PropertyInfo Aperture,
    [property: JsonPropertyName("focusMode")] PropertyInfo FocusMode
);

public record PropertyInfo(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("choices")] string[] Choices
);

public record SetValuePayload(
    [property: JsonPropertyName("value")] string Value
);

public record CaptureBulbPayload(
    [property: JsonPropertyName("duration")] float Duration
);

public record LiveViewStartPayload(
    [property: JsonPropertyName("port")] int Port
);

public record FocusPayload(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("step")] int Step
);

public record SetWidgetPayload(
    [property: JsonPropertyName("widget")] string Widget,
    [property: JsonPropertyName("value")] string Value
);

public record CaptureCompletePayload(
    [property: JsonPropertyName("url")] string Url
);

public record CameraStatusPayload(
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("battery")] string? Battery,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("iso")] string? Iso,
    [property: JsonPropertyName("shutterSpeed")] string? ShutterSpeed,
    [property: JsonPropertyName("aperture")] string? Aperture,
    [property: JsonPropertyName("focusMode")] string? FocusMode
);

public record CameraListEntry(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("port")] string Port
);

#endregion

#region Mount Payloads

public record MountMovePayload(
    [property: JsonPropertyName("axis")] string Axis,
    [property: JsonPropertyName("position")] int Position
);

public record MountMoveRelativePayload(
    [property: JsonPropertyName("axis")] string Axis,
    [property: JsonPropertyName("offset")] int Offset
);

public record MountLinearPayload(
    [property: JsonPropertyName("xRate")] float XRate,
    [property: JsonPropertyName("yRate")] float YRate,
    [property: JsonPropertyName("zRate")] float ZRate
);

public record MountTrackingPayload(
    [property: JsonPropertyName("ra")] float Ra,
    [property: JsonPropertyName("dec")] float Dec
);

public record MountAlignStarPayload(
    [property: JsonPropertyName("ra")] float Ra,
    [property: JsonPropertyName("dec")] float Dec
);

public record MountStatusPayload(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("z")] int Z,
    [property: JsonPropertyName("temperature")] float Temperature,
    [property: JsonPropertyName("motorsEnabled")] bool MotorsEnabled,
    [property: JsonPropertyName("motorsPaused")] bool MotorsPaused,
    [property: JsonPropertyName("celestialTracking")] bool CelestialTracking,
    [property: JsonPropertyName("fanSpeed")] int FanSpeed
);

public record MountPositionPayload(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("z")] int Z
);

public record MountAlignmentStatusPayload(
    [property: JsonPropertyName("isAligned")] bool IsAligned,
    [property: JsonPropertyName("pointCount")] int PointCount,
    [property: JsonPropertyName("latitude")] float Latitude,
    [property: JsonPropertyName("longitude")] float Longitude,
    [property: JsonPropertyName("quality")] string? Quality,
    [property: JsonPropertyName("averageResidualArcmin")] double? AverageResidualArcmin,
    [property: JsonPropertyName("averageResidualPixels")] double? AverageResidualPixels,
    [property: JsonPropertyName("maxPairErrorDeg")] double? MaxPairErrorDeg,
    [property: JsonPropertyName("stepLossPercent")] double? StepLossPercent,
    [property: JsonPropertyName("activeStarCount")] int? ActiveStarCount,
    [property: JsonPropertyName("rejectedCount")] int? RejectedCount,
    [property: JsonPropertyName("stars")] AlignmentStarInfo[]? Stars
);

public record AlignmentStarInfo(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("ra")] float Ra,
    [property: JsonPropertyName("dec")] float Dec,
    [property: JsonPropertyName("residualArcmin")] double ResidualArcmin,
    [property: JsonPropertyName("excluded")] bool Excluded,
    [property: JsonPropertyName("exclusionReason")] string? ExclusionReason
);
#endregion

#region System Payloads

public record SystemInfoPayload(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("uptime")] long Uptime
);

#endregion

#region Widget Payloads

public record WidgetInfo(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("choices")] string[]? Choices,
    [property: JsonPropertyName("range")] WidgetRange? Range
);

public record WidgetRange(
    [property: JsonPropertyName("min")] float Min,
    [property: JsonPropertyName("max")] float Max,
    [property: JsonPropertyName("step")] float Step
);

#endregion

#region Plate-Solve Payloads

public record AutoCenterPayload(
    [property: JsonPropertyName("ra")] float Ra,
    [property: JsonPropertyName("dec")] float Dec,
    [property: JsonPropertyName("tolerance")] float Tolerance = 15.0f
);

public record AutoCalibratePayload(
    [property: JsonPropertyName("altSteps")] int AltSteps = 4,
    [property: JsonPropertyName("azSteps")] int AzSteps = 5,
    [property: JsonPropertyName("wideSweep")] bool WideSweep = true
);

public record GuidedTrackingPayload(
    [property: JsonPropertyName("ra")] float Ra,
    [property: JsonPropertyName("dec")] float Dec,
    [property: JsonPropertyName("interval")] int Interval = 60,
    [property: JsonPropertyName("maxCorrections")] int MaxCorrections = 3  // 0 = unlimited
);

/// <summary>Broadcast after every guide check (whether or not a correction was applied).</summary>
public record GuidedTrackingProgressPayload(
    [property: JsonPropertyName("check")] int Check,
    [property: JsonPropertyName("maxCorrections")] int MaxCorrections,
    [property: JsonPropertyName("corrections")] int Corrections,
    [property: JsonPropertyName("driftPx")] double DriftPx,
    [property: JsonPropertyName("driftArcmin")] double DriftArcmin,
    [property: JsonPropertyName("correctionApplied")] bool CorrectionApplied,
    [property: JsonPropertyName("corrXArcsec")] int? CorrXArcsec,
    [property: JsonPropertyName("corrZArcsec")] int? CorrZArcsec
);

/// <summary>Broadcast once when the guide loop exits for any reason.</summary>
public record GuidedTrackingCompletePayload(
    [property: JsonPropertyName("checks")] int Checks,
    [property: JsonPropertyName("corrections")] int Corrections,
    [property: JsonPropertyName("reason")] string Reason  // "maxCorrectionsReached" | "stopped" | "error"
);

public record PlateSolveConfigPayload(
    [property: JsonPropertyName("focalLengthMm")] float? FocalLengthMm = null,
    [property: JsonPropertyName("pixelSizeUm")] float? PixelSizeUm = null,
    [property: JsonPropertyName("focalLength")] float? FocalLength = null,
    [property: JsonPropertyName("pixelSize")] float? PixelSize = null
);

#endregion
