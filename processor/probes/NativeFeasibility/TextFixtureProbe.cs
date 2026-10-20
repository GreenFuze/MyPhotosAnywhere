using SkiaSharp;

namespace Mpa.Processor.Probes;

/// <summary>Creates known synthetic text for OCR smoke checks; contains no user media.</summary>
internal sealed class TextFixtureProbe(string newPath)
{
    public void Write()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1200, 240, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName("Arial")
            ?? throw new InvalidOperationException("Synthetic OCR fixture requires an available test font");
        using var font = new SKFont(typeface, 64);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText("MPA PROCESSOR 6045", 50, 140, SKTextAlign.Left, font, paint);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoded.SaveTo(output);
        output.Flush(flushToDisk: true);
        Console.WriteLine("Synthetic OCR fixture created; no user image accessed.");
    }
}
