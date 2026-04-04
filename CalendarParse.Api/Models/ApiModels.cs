using CalendarParse.Models;

namespace CalendarParse.Api;

public class ProcessRequest
{
    /// <summary>Base64-encoded image bytes (JPEG or PNG, post-EXIF-rotation).</summary>
    public string ImageBase64 { get; set; } = string.Empty;

    /// <summary>Employee name as it appears on the schedule. Used to filter shifts client-side.</summary>
    public string EmployeeName { get; set; } = string.Empty;
}

public class ProcessResponse
{
    /// <summary>All shifts found in the image, each with an optional EstimatedBounds overlay position.</summary>
    public List<ShiftData> Shifts { get; set; } = [];

    /// <summary>Natural pixel width of the source image. Used by the mobile overlay to map bounds → screen coords.</summary>
    public int ImageWidth  { get; set; }

    /// <summary>Natural pixel height of the source image.</summary>
    public int ImageHeight { get; set; }
}

public class ConfirmRequest
{
    /// <summary>Employee-corrected shifts after overlay confirmation.</summary>
    public List<ShiftData> Shifts { get; set; } = [];
}

// ── Async job endpoints ──────────────────────────────────────────────────────

public class SubmitRequest
{
    public string ImageBase64  { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
}

public class SubmitResponse
{
    public string JobId { get; set; } = string.Empty;
}

public class JobStatusResponse
{
    /// <summary>One of: "submitted", "processing", "done", "error".</summary>
    public string  Status { get; set; } = string.Empty;
    public string? Error  { get; set; }
}

public class JobResultResponse
{
    public List<ShiftData> Shifts      { get; set; } = [];
    public int             ImageWidth  { get; set; }
    public int             ImageHeight { get; set; }
}
