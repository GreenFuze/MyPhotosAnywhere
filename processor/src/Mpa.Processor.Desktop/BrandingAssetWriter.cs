using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace Mpa.Processor.Desktop;

/// <summary>Build tool for the original Android vector emblem, with no downloaded/generated logo.</summary>
internal static class BrandingAssetWriter
{
    private static readonly int[] Sizes = [16, 32, 48, 64, 128, 256];
    public static void Write(string androidVector, string outputIcon)
    {
        using var reader = XmlReader.Create(androidVector, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32 * 1024 });
        var document = XDocument.Load(reader);
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var paths = document.Root?.Elements("path").ToArray() ?? throw new InvalidDataException("Missing original MPA vector.");
        if (paths.Length != 5) throw new InvalidDataException("Original emblem structure changed; review its asset conversion.");
        var images = new List<byte[]>();
        foreach (var size in Sizes)
        {
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Branding raster surface could not be allocated.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse("#125E53"));
            canvas.Scale(size / 108f);
            using var paint = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            foreach (var pathElement in paths)
            {
                using var path = SKPath.ParseSvgPathData(pathElement.Attribute(android + "pathData")?.Value
                    ?? throw new InvalidDataException("Missing original vector path."));
                if (path is null) throw new InvalidDataException("Invalid original vector path.");
                paint.Style = SKPaintStyle.Fill;
                paint.Color = SKColor.Parse(pathElement.Attribute(android + "fillColor")?.Value ?? "#00000000");
                if (paint.Color.Alpha != 0) canvas.DrawPath(path, paint);
                if (pathElement.Attribute(android + "strokeColor") is { } stroke)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.Color = SKColor.Parse(stroke.Value);
                    paint.StrokeWidth = float.Parse(pathElement.Attribute(android + "strokeWidth")!.Value, CultureInfo.InvariantCulture);
                    canvas.DrawPath(path, paint);
                }
            }
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            images.Add(encoded.ToArray());
        }
        var destination = Path.GetFullPath(outputIcon);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
        var offset = 6 + 16 * Sizes.Length;
        for (var index = 0; index < Sizes.Length; index++)
        {
            writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index])); writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[index].Length); writer.Write(offset); offset = checked(offset + images[index].Length);
        }
        foreach (var image in images) writer.Write(image);
        output.Flush(flushToDisk: true);
    }
}
