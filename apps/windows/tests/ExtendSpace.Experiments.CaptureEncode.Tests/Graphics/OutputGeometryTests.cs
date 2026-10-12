using ExtendSpace.Experiments.CaptureEncode.Graphics;

namespace ExtendSpace.Experiments.CaptureEncode.Tests.Graphics;

public class OutputGeometryTests
{
    [Theory]
    [InlineData(2560, 1600)]
    [InlineData(1366, 768)]
    public void Without_a_requested_size_an_even_sized_source_is_encoded_as_it_is(int width, int height)
    {
        OutputGeometry geometry = Plan(width, height, requested: null);

        Assert.Equal(new OutputGeometry(width, height, width, height, 0, 0, width, height), geometry);
        Assert.False(geometry.IsScaled);
        Assert.False(geometry.HasBars);
    }

    [Theory]
    [InlineData(1919, 1081, 1918, 1080)]
    [InlineData(3, 2, 2, 2)]
    public void Without_a_requested_size_odd_dimensions_lose_one_row_or_column_rather_than_being_scaled(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        OutputGeometry geometry = Plan(width, height, requested: null);

        Assert.Equal((expectedWidth, expectedHeight), (geometry.OutputWidth, geometry.OutputHeight));
        Assert.Equal((expectedWidth, expectedHeight), (geometry.SourceWidth, geometry.SourceHeight));
        Assert.False(geometry.IsScaled);
        Assert.False(geometry.HasBars);
    }

    [Theory]
    [InlineData(1, 1080)]
    [InlineData(1920, 1)]
    [InlineData(0, 0)]
    public void A_source_with_no_even_sized_area_cannot_be_encoded(int width, int height)
    {
        Assert.Null(OutputGeometry.Create(width, height, requested: null));
        Assert.Null(OutputGeometry.Create(0, height, requested: (1280, 720)));
    }

    [Theory]
    [InlineData(2560, 1600, 1920, 1200)]
    [InlineData(2560, 1600, 1280, 800)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1280, 800, 2560, 1600)]
    public void A_requested_size_of_the_same_shape_is_filled_exactly(int sourceWidth, int sourceHeight, int width, int height)
    {
        OutputGeometry geometry = Plan(sourceWidth, sourceHeight, (width, height));

        Assert.Equal(new OutputGeometry(sourceWidth, sourceHeight, width, height, 0, 0, width, height), geometry);
        Assert.True(geometry.IsScaled);
        Assert.False(geometry.HasBars);
    }

    [Fact]
    public void A_wider_request_gets_bars_left_and_right()
    {
        // 16:10 into 16:9: the height is the tight fit.
        OutputGeometry geometry = Plan(2560, 1600, (1280, 720));

        Assert.Equal((1152, 720), (geometry.PictureWidth, geometry.PictureHeight));
        Assert.Equal((64, 0), (geometry.PictureX, geometry.PictureY));
        Assert.True(geometry.HasBars);
        Assert.True(geometry.IsScaled);
    }

    [Fact]
    public void A_taller_request_gets_bars_above_and_below()
    {
        // 16:9 into 4:3: the width is the tight fit.
        OutputGeometry geometry = Plan(1920, 1080, (1024, 768));

        Assert.Equal((1024, 576), (geometry.PictureWidth, geometry.PictureHeight));
        Assert.Equal((0, 96), (geometry.PictureX, geometry.PictureY));
        Assert.True(geometry.HasBars);
    }

    [Theory]
    [InlineData(2560, 1600, 1280, 720)]
    [InlineData(1366, 768, 1920, 1200)]
    [InlineData(1919, 1081, 1280, 720)]
    [InlineData(3440, 1440, 1920, 1080)]
    [InlineData(1080, 1920, 1920, 1080)]
    [InlineData(2136, 3200, 854, 480)]
    public void The_picture_always_lies_inside_the_frame_on_even_coordinates_and_keeps_its_shape(
        int sourceWidth,
        int sourceHeight,
        int width,
        int height)
    {
        OutputGeometry geometry = Plan(sourceWidth, sourceHeight, (width, height));

        Assert.Equal((width, height), (geometry.OutputWidth, geometry.OutputHeight));
        Assert.Equal((sourceWidth, sourceHeight), (geometry.SourceWidth, geometry.SourceHeight));
        Assert.All(
            new[] { geometry.PictureX, geometry.PictureY, geometry.PictureWidth, geometry.PictureHeight },
            value => Assert.Equal(0, value % 2));
        Assert.True(geometry.PictureX >= 0 && geometry.PictureY >= 0);
        Assert.True(geometry.PictureX + geometry.PictureWidth <= width);
        Assert.True(geometry.PictureY + geometry.PictureHeight <= height);

        // One dimension fills the frame, give or take the rounding to an even number.
        Assert.True(width - geometry.PictureWidth <= 2 || height - geometry.PictureHeight <= 2);

        // Rounding each dimension to an even number moves the shape by at most a pixel or so.
        double sourceShape = (double)sourceWidth / sourceHeight;
        double lower = (geometry.PictureWidth - 2.0) / (geometry.PictureHeight + 2.0);
        double upper = (geometry.PictureWidth + 2.0) / (geometry.PictureHeight - 2.0);
        Assert.InRange(sourceShape, lower, upper);
    }

    [Theory]
    [InlineData(1920, 1200, true)]
    [InlineData(128, 128, true)]
    [InlineData(4096, 4096, true)]
    [InlineData(1921, 1200, false)]
    [InlineData(1920, 1201, false)]
    [InlineData(126, 720, false)]
    [InlineData(1280, 4098, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1280, 720, false)]
    public void Requested_sizes_must_be_even_and_within_range(int width, int height, bool expected)
    {
        Assert.Equal(expected, OutputGeometry.IsValidRequest(width, height));
    }

    private static OutputGeometry Plan(int sourceWidth, int sourceHeight, (int Width, int Height)? requested)
    {
        OutputGeometry? geometry = OutputGeometry.Create(sourceWidth, sourceHeight, requested);
        Assert.NotNull(geometry);
        return geometry;
    }
}
