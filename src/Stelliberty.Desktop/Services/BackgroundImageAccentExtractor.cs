using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Stelliberty.Desktop.Services;

internal static class BackgroundImageAccentExtractor
{
    private const int SampleSize = 48;
    private const int HueBucketCount = 24;
    private const double MinimumChroma = 0.025;

    public static Color? Extract(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var scale = SampleSize / (double)Math.Max(size.Width, size.Height);
        using var scaled = scale < 1
            ? bitmap.CreateScaledBitmap(new PixelSize(
                Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale))))
            : null;
        var sample = scaled ?? bitmap;
        using var rgba = new WriteableBitmap(sample.PixelSize, sample.Dpi, PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var framebuffer = rgba.Lock();
        sample.CopyPixels(framebuffer);
        var pixels = new byte[framebuffer.RowBytes * framebuffer.Size.Height];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

        var buckets = new HueBucket[HueBucketCount];
        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            for (var x = 0; x < framebuffer.Size.Width; x++)
            {
                var index = y * framebuffer.RowBytes + x * 4;
                var alpha = pixels[index + 3];
                if (alpha < 128)
                {
                    continue;
                }

                var color = Color.FromRgb(pixels[index], pixels[index + 1], pixels[index + 2]);
                var oklch = AccentColorPaletteGenerator.ToOklch(color);
                if (oklch.Chroma < MinimumChroma)
                {
                    continue;
                }

                var bucketIndex = (int)(oklch.Hue * HueBucketCount / 360 + 0.5) % HueBucketCount;
                buckets[bucketIndex].Add(color, alpha);
            }
        }

        var dominant = buckets.MaxBy(bucket => bucket.Weight);
        return dominant.Weight == 0
            ? null
            : Color.FromRgb(
                (byte)(dominant.Red / dominant.Weight),
                (byte)(dominant.Green / dominant.Weight),
                (byte)(dominant.Blue / dominant.Weight));
    }

    private struct HueBucket
    {
        public int Weight;
        public int Red;
        public int Green;
        public int Blue;

        public void Add(Color color, byte alpha)
        {
            Weight += alpha;
            Red += color.R * alpha;
            Green += color.G * alpha;
            Blue += color.B * alpha;
        }
    }
}
