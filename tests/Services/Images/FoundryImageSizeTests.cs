using FluentAssertions;
using PKS.Infrastructure.Services.Images;
using Xunit;

namespace PKS.CLI.Tests.Services.Images;

public class FoundryImageSizeTests
{
    private static ImageGenerationRequest Req(string aspect = "auto", string? resolution = null, string? size = null,
        string model = "gpt-image-2", params string[] inputs)
        => new("p", model, aspect, resolution, inputs, Size: size);

    [Theory]
    [InlineData("3:2", "2048", 2048, 1360)]
    [InlineData("16:9", null, 1536, 864)]
    [InlineData("2:3", "2048", 1360, 2048)]
    [InlineData("21:9", "3840", 3840, 1648)]
    public void Aspect_ratio_maps_to_a_valid_gpt_image_size(string aspect, string? resolution, int w, int h)
    {
        var (width, height) = AzureFoundryImageProvider.ResolveSize(Req(aspect, resolution));

        width.Should().Be(w);
        height.Should().Be(h);
    }

    [Theory]
    [InlineData(1.5, 2048)]
    [InlineData(2.3, 3840)]
    [InlineData(0.4, 1024)]
    [InlineData(5.0, 3840)]
    [InlineData(1.0, 4096)]
    public void Fitted_sizes_stay_within_the_gpt_image_constraints(double ratio, int longEdge)
    {
        var (w, h) = AzureFoundryImageProvider.FitSize(ratio, longEdge);

        (w % 16).Should().Be(0);
        (h % 16).Should().Be(0);
        Math.Max(w, h).Should().BeLessThanOrEqualTo(3840);
        ((double)Math.Max(w, h) / Math.Min(w, h)).Should().BeLessThanOrEqualTo(3.0 + 0.05);
        ((long)w * h).Should().BeInRange(655_360, 8_294_400);
    }

    [Fact]
    public void Explicit_size_wins()
        => AzureFoundryImageProvider.ResolveSize(Req("16:9", "2048", "1200x800")).Should().Be((1200, 800));

    [Fact]
    public void Dall_e_keeps_the_classic_sizes()
        => AzureFoundryImageProvider.ResolveSize(Req("16:9", model: "dall-e-3")).Should().Be((1792, 1024));

    [Fact]
    public void Auto_on_an_edit_follows_the_first_input()
    {
        var png = Path.Combine(Path.GetTempPath(), $"pks-size-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(png, Png(3000, 2000));

            AzureFoundryImageProvider.ResolveSize(Req("auto", "2048", inputs: png)).Should().Be((2048, 1360));
        }
        finally { File.Delete(png); }
    }

    [Fact]
    public void Reads_jpeg_dimensions_from_the_frame_header()
    {
        var jpg = Path.Combine(Path.GetTempPath(), $"pks-size-{Guid.NewGuid():N}.jpg");
        try
        {
            // SOI, an APP0 segment to skip, then SOF0 with height 480 and width 640.
            File.WriteAllBytes(jpg, new byte[]
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x06, 0x4A, 0x46, 0x49, 0x46,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x01, 0x01, 0x11, 0x00,
                0xFF, 0xD9,
            });

            ImageDimensions.TryRead(jpg, out var w, out var h).Should().BeTrue();
            (w, h).Should().Be((640, 480));
        }
        finally { File.Delete(jpg); }
    }

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }
}
