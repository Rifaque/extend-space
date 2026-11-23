using ExtendSpace.Core.Input;
using ExtendSpace.Media.Graphics;
using ExtendSpace.Media.Input;

namespace ExtendSpace.Media.Tests;

public sealed class InputGeometryAgreementTests
{
    /// <summary>
    /// Touch mapping (D83) removes the encoder's letterbox with its own copy of the arithmetic,
    /// because ExtendSpace.Core cannot reference the encoder. This pins the two together.
    /// </summary>
    [Fact]
    public void The_input_mapping_uses_exactly_the_encoders_picture_rectangle()
    {
        int[] sizes = [640, 720, 768, 800, 1024, 1080, 1200, 1280, 1366, 1440, 1600, 1920, 2160, 2560, 2880, 3440, 3840];
        (int, int)[] streams = [(1920, 1080), (1280, 720)];
        int checkedCount = 0;
        foreach (int width in sizes)
        {
            foreach (int height in sizes)
            {
                foreach ((int streamWidth, int streamHeight) in streams)
                {
                    OutputGeometry encoder = OutputGeometry.Create(width, height, (streamWidth, streamHeight))!;
                    Assert.Equal(
                        (encoder.PictureX, encoder.PictureY, encoder.PictureWidth, encoder.PictureHeight),
                        InputGeometry.Picture(width, height, streamWidth, streamHeight));
                    checkedCount++;
                }
            }
        }

        Assert.Equal(sizes.Length * sizes.Length * streams.Length, checkedCount);
    }

    [Fact]
    public void The_monitor_locator_finds_the_main_display_on_this_PC()
    {
        ScreenRect? main = new DesktopMonitorLocator().Locate(null);
        Assert.NotNull(main);
        Assert.True(main.Value.Width > 0 && main.Value.Height > 0);
        Assert.Null(new DesktopMonitorLocator().Locate(@"\\.\NO_SUCH_DISPLAY"));
    }
}

public sealed class VirtualScreenTests
{
    // This PC's layout when the defect was found: a laptop panel (2560 x 1600) to the left of the main
    // monitor (2560 x 1440), so the virtual screen starts at (-2560, -72).
    [Theory]
    [InlineData(1280, 720, -2560, -72, 3840, 792)]
    [InlineData(-1280, 648, -2560, -72, 1280, 720)]
    [InlineData(960, 540, 0, 0, 960, 540)]
    public void Touch_points_are_relative_to_the_virtual_screens_top_left(int x, int y, int left, int top, int expectedX, int expectedY) =>
        Assert.Equal(new ScreenPoint(expectedX, expectedY), VirtualScreen.ToTouchSpace(new ScreenPoint(x, y), left, top));
}
