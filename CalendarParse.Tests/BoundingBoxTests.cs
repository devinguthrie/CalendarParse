using CalendarParse.Models;
using Xunit;

namespace CalendarParse.Tests;

public class BoundingBoxTests
{
    [Fact]
    public void Rect_CenterX_CenterY_Correct()
    {
        var r = new Rect(10, 20, 30, 40);
        Assert.Equal(25, r.CenterX); // 10 + 30/2
        Assert.Equal(40, r.CenterY); // 20 + 40/2
    }

    [Fact]
    public void Rect_ZeroWidthHeight_CenterIsOrigin()
    {
        var r = new Rect(0, 0, 0, 0);
        Assert.Equal(0, r.CenterX);
        Assert.Equal(0, r.CenterY);
    }

    [Fact]
    public void Rect_NegativeWidthHeight_CenterMath()
    {
        var r = new Rect(10, 10, -4, -6);
        Assert.Equal(8, r.CenterX); // 10 + (-4/2) = 8
        Assert.Equal(7, r.CenterY); // 10 + (-6/2) = 7
    }

    [Fact]
    public void BoundingBox_Properties_SetAndGet()
    {
        var b = new BoundingBox { X = 1, Y = 2, Width = 3, Height = 4 };
        Assert.Equal(1, b.X);
        Assert.Equal(2, b.Y);
        Assert.Equal(3, b.Width);
        Assert.Equal(4, b.Height);
    }
}
