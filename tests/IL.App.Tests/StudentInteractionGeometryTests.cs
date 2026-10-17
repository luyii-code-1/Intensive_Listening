using Avalonia;
using IL.App.Views;
using Xunit;

namespace IL.App.Tests;

public sealed class StudentInteractionGeometryTests
{
    [Fact]
    public void HiddenWordHitTargetExpandsByTwentyFivePercentAcrossBothAxes()
    {
        var size = new Size(100, 40);
        Assert.NotNull(StudentInteractionGeometry.ClozeHitDistance(size, new Point(-12.4, 20), true));
        Assert.NotNull(StudentInteractionGeometry.ClozeHitDistance(size, new Point(112.4, 20), true));
        Assert.NotNull(StudentInteractionGeometry.ClozeHitDistance(size, new Point(50, -4.9), true));
        Assert.NotNull(StudentInteractionGeometry.ClozeHitDistance(size, new Point(50, 44.9), true));
        Assert.Null(StudentInteractionGeometry.ClozeHitDistance(size, new Point(-12.6, 20), true));
        Assert.Null(StudentInteractionGeometry.ClozeHitDistance(size, new Point(50, 45.1), true));
    }
    [Fact]
    public void DirectHitsTakePriorityOverExpandedNeighborsAndVisibleWordsKeepTheirBounds()
    {
        var size = new Size(100, 40);
        var direct = StudentInteractionGeometry.ClozeHitDistance(size, new Point(98, 20), false);
        var neighboring = StudentInteractionGeometry.ClozeHitDistance(size, new Point(-2, 20), true);
        Assert.Equal(0d, direct); Assert.True(direct < neighboring);
        Assert.Null(StudentInteractionGeometry.ClozeHitDistance(size, new Point(-2, 20), false));
        Assert.Null(StudentInteractionGeometry.ClozeHitDistance(default, default, true));
    }
}
