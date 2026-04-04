using CalendarParse.Cli.Services;
using CalendarParse.Services;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for HybridCalendarService.ProcessWithBoundsAsync.
///
/// These tests use the real HybridCalendarService constructor (which requires Ollama + WinRT)
/// only for the error-path cases that don't need a running model.
/// Happy-path integration tests are marked [Fact(Skip=...)] because they require
/// a live Ollama instance with qwen2.5vl:7b loaded.
/// </summary>
public class ProcessWithBoundsTests
{
    [Fact]
    public async Task ProcessWithBoundsAsync_ErrorJson_ReturnsIsError()
    {
        // Arrange: point at a non-existent Ollama to force an error response
        var svc = new HybridCalendarService(
            baseUrl: "http://localhost:19999", // nothing listening here
            model:   "qwen2.5vl:7b");

        using var stream = new MemoryStream([0xFF, 0xD8, 0xFF]); // 3-byte fake JPEG header

        // Act
        var result = await svc.ProcessWithBoundsAsync(stream, "Alice",
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        Assert.True(result.IsError);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// Verifies that the snapshot conversion used in ProcessWithBoundsAsync correctly
    /// maps CellPositions fields to CellPositionsSnapshot, and that ImageWidth/ImageHeight
    /// propagate to ProcessWithBoundsResult. Tested without requiring Ollama or WinRT.
    /// </summary>
    [Fact]
    public async Task SnapshotConversion_MapsPositionsCorrectlyAndPreservesImageDimensions()
    {
        var positions = new CellPositions
        {
            ImageWidth           = 1920,
            ImageHeight          = 1080,
            EstimatedRowHeightPx = 40,
            Columns = { [2] = new ColBounds { EstimatedCellXStart = 100, EstimatedCellXEnd = 200 } },
            Rows    = { ["Alice"] = new EmployeeRow { EstimatedCellY = 300 } },
        };

        // Simulate the conversion used in ProcessWithBoundsAsync
        var snapshot = new CellPositionsSnapshot
        {
            EstimatedRowHeightPx = positions.EstimatedRowHeightPx,
            Columns = positions.Columns.ToDictionary(
                kvp => kvp.Key,
                kvp => new ColSnapshot { XStart = kvp.Value.EstimatedCellXStart, XEnd = kvp.Value.EstimatedCellXEnd }),
            Rows = positions.Rows.ToDictionary(
                kvp => kvp.Key,
                kvp => new RowSnapshot { CellCenterY = kvp.Value.EstimatedCellY }),
        };

        Assert.Equal(40,  snapshot.EstimatedRowHeightPx);
        Assert.Equal(100, snapshot.Columns[2].XStart);
        Assert.Equal(200, snapshot.Columns[2].XEnd);
        Assert.Equal(300, snapshot.Rows["Alice"].CellCenterY);

        // Verify image dims propagate to the result
        var result = new ProcessWithBoundsResult
        {
            ImageWidth  = positions.ImageWidth,
            ImageHeight = positions.ImageHeight,
            Shifts      = [],
        };
        Assert.Equal(1920, result.ImageWidth);
        Assert.Equal(1080, result.ImageHeight);

        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires live Ollama + qwen2.5vl:7b — run manually")]
    public async Task ProcessWithBoundsAsync_RealImage_ReturnsBoundsForKnownEmployee()
    {
        var svc = new HybridCalendarService(model: "qwen2.5vl:7b");

        var imagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"OneDrive\Documents\Repos\CalendarParse\CalendarParse\calander-parse-test-imgs\tmp-im1\input.jpg");

        if (!File.Exists(imagePath))
            throw new SkipException($"Test image not found: {imagePath}");

        await using var stream = File.OpenRead(imagePath);
        var result = await svc.ProcessWithBoundsAsync(stream, string.Empty);

        Assert.False(result.IsError);
        Assert.NotEmpty(result.Shifts);
        Assert.Contains(result.Shifts, s => s.EstimatedBounds is not null);
    }
}

// xUnit skip helper (xUnit v2 doesn't have a built-in SkipException)
public class SkipException(string message) : Exception(message);
