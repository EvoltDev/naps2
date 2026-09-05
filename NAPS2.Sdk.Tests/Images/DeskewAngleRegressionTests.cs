using NAPS2.Images.ImageSharp;
using Xunit;

namespace NAPS2.Sdk.Tests.Images;

public class DeskewAngleRegressionTests
{
    [Theory]
    [MemberData(nameof(AngleCases))]
    public void DetectsAngleAcrossPixelFormats(string pattern, ImagePixelFormat pixelFormat, double expectedAngle)
    {
        using var source = CreatePattern(pattern);
        using var image = source.CopyWithPixelFormat(pixelFormat);

        Assert.Equal(expectedAngle, ImageAnalysis.GetSkewAngle(image), 12);
    }

    public static IEnumerable<object[]> AngleCases()
    {
        var patterns = new (string name, double angle)[]
        {
            ("blank", -20d),
            ("solid", -20d),
            ("horizontal", 1.4210854715202005E-15d),
            ("ascending", -11.301176470588231d),
            ("descending", 8.104878048780488d),
            ("crossed", -11.352941176470582d),
            ("noise", 16.119230769230786d),
            ("border", -20d),
            ("sparse", -0.020895522388058196d),
        };
        foreach (var (name, angle) in patterns)
        {
            foreach (var pixelFormat in new[]
                     { ImagePixelFormat.RGB24, ImagePixelFormat.ARGB32, ImagePixelFormat.Gray8, ImagePixelFormat.BW1 })
            {
                yield return new object[] { name, pixelFormat, angle };
            }
        }
    }

    private static unsafe IMemoryImage CreatePattern(string pattern)
    {
        const int width = 257;
        const int height = 193;
        var image = new ImageSharpImageContext().Create(width, height, ImagePixelFormat.Gray8);
        using var imageLock = image.Lock(LockMode.ReadWrite, out var data);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool marked = pattern switch
                {
                    "blank" => false,
                    "solid" => true,
                    "horizontal" => y % 17 < 2,
                    "ascending" => (y + x / 5) % 17 < 2,
                    "descending" => (y + width - x / 7) % 17 < 2,
                    "crossed" => (y + x / 5) % 17 < 2 || (y + width - x / 5) % 17 < 2,
                    "noise" => ((uint)(y * width + x) * 2654435761u >> 28) < 4,
                    "border" => x < 2 || y < 2 || x >= width - 2 || y >= height - 2,
                    "sparse" => x % 43 == 0 && y % 37 == 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(pattern))
                };
                data.ptr[y * data.stride + x] = marked ? (byte)0 : (byte)255;
            }
        }
        return image;
    }
}
