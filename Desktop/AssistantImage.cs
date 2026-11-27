using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CoreShift.Desktop;

internal sealed record AssistantImage(string Name, byte[] Png, BitmapSource Preview)
{
    private const int AttachmentMaxBytes = 12 * 1024 * 1024;
    private const int MaxDimension = 1440;

    internal static AssistantImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > AttachmentMaxBytes)
            throw new InvalidDataException("The attachment is too large or empty.");

        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder))
            throw new InvalidDataException("Unsupported image format.");
        var frame = decoder.Frames[0];
        CheckDimensions(frame);
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        object? orientation = decoder is JpegBitmapDecoder && frame.Metadata is BitmapMetadata metadata
            ? metadata.GetQuery("/app1/ifd/{ushort=274}") : null;
        int rotation = orientation switch
        {
            ushort and 3 => 180,
            ushort and 6 => 90,
            ushort and 8 => 270,
            _ => 0
        };

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.Rotation = (Rotation)(rotation / 90);
        if (width >= height)
            bitmap.DecodePixelWidth = Math.Min(MaxDimension, width);
        else
            bitmap.DecodePixelHeight = Math.Min(MaxDimension, height);
        bitmap.EndInit();
        return Encode(Path.GetFileName(path), bitmap);
    }

    internal static AssistantImage FromClipboard(string name, BitmapSource bitmap)
    {
        CheckDimensions(bitmap);
        double scale = Math.Min(1, (double)MaxDimension / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        return Encode(name, scale == 1 ? bitmap : new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)));
    }

    private static void CheckDimensions(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 40_000_000)
            throw new InvalidDataException("Unsupported image dimensions.");
    }

    private static AssistantImage Encode(string name, BitmapSource bitmap)
    {
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        double scale = Math.Min(1, 160d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var preview = new CachedBitmap(new TransformedBitmap(pixels, new ScaleTransform(scale, scale)),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        preview.Freeze();
        return new(name, stream.ToArray(), preview);
    }
}
