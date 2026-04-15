using CalendarParse.Services;

namespace CalendarParse.Tests;

public class NotificationReceivedEventArgsTests
{
    [Fact]
    public void DefaultConstruction_SenderName_IsEmpty()
    {
        var args = new NotificationReceivedEventArgs();
        Assert.Equal(string.Empty, args.SenderName);
    }

    [Fact]
    public void DefaultConstruction_SourcePackage_IsEmpty()
    {
        var args = new NotificationReceivedEventArgs();
        Assert.Equal(string.Empty, args.SourcePackage);
    }

    [Fact]
    public void DefaultConstruction_ImageBytes_IsNull()
    {
        var args = new NotificationReceivedEventArgs();
        Assert.Null(args.ImageBytes);
    }

    [Fact]
    public void DefaultConstruction_ReceivedAt_IsRecent()
    {
        var before = DateTime.Now;
        var args   = new NotificationReceivedEventArgs();
        var after  = DateTime.Now;

        Assert.InRange(args.ReceivedAt, before, after);
    }

    [Fact]
    public void InitProperties_ArePreserved()
    {
        var stamp = new DateTime(2026, 4, 12, 9, 30, 0);
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG magic bytes
        var args  = new NotificationReceivedEventArgs
        {
            SenderName    = "Boss",
            SourcePackage = "com.google.android.apps.messaging",
            ReceivedAt    = stamp,
            ImageBytes    = bytes,
        };

        Assert.Equal("Boss",                               args.SenderName);
        Assert.Equal("com.google.android.apps.messaging",  args.SourcePackage);
        Assert.Equal(stamp,                                args.ReceivedAt);
        Assert.Same(bytes,                                 args.ImageBytes);
    }

    [Fact]
    public void ImageBytes_NonNull_MeansImageAvailable()
    {
        var args = new NotificationReceivedEventArgs
        {
            ImageBytes = new byte[2048]
        };
        Assert.NotNull(args.ImageBytes);
        Assert.True(args.ImageBytes.Length > 1024,
            "ImageBytes are expected to be > 1 KB when present (mirroring the extraction sanity check)");
    }

    [Fact]
    public void IsEventArgs_AllowsTypedSubscription()
    {
        // Verifies the class can be used as EventArgs<T> — i.e. the inheritance is correct
        EventHandler<NotificationReceivedEventArgs>? handler = null;
        var args = new NotificationReceivedEventArgs { SenderName = "Test" };

        string captured = string.Empty;
        handler += (_, e) => captured = e.SenderName;
        handler?.Invoke(this, args);

        Assert.Equal("Test", captured);
    }
}
