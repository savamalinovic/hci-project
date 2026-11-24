using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PaperlessDesktop.Services;

internal sealed class DocumentPreviewRenderer : IDisposable
{
    private const int MaxPreviewDimension = 2400;
    private readonly PdfDocument? pdf;
    private readonly IRandomAccessStream? pdfInput;
    private readonly BitmapSource? image;
    private bool isDisposed;

    private DocumentPreviewRenderer(PdfDocument pdf, IRandomAccessStream input)
    {
        this.pdf = pdf;
        pdfInput = input;
        PageCount = checked((int)pdf.PageCount);
    }

    private DocumentPreviewRenderer(BitmapSource image)
    {
        this.image = image;
        PageCount = 1;
    }

    public int PageCount { get; }

    public static async Task<DocumentPreviewRenderer?> OpenAsync(
        byte[] content, string? mediaType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            content.AsSpan().StartsWith("%PDF-"u8))
        {
            return await OpenPdfAsync(content, cancellationToken).ConfigureAwait(false);
        }

        if (!IsBitmap(content, mediaType))
        {
            return null;
        }

        var bitmap = await Task.Run(() => DecodeImage(content), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new DocumentPreviewRenderer(bitmap);
    }

    public async Task<BitmapSource> RenderPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        cancellationToken.ThrowIfCancellationRequested();

        if (image is not null)
        {
            return image;
        }

        using var page = pdf!.GetPage((uint)pageIndex);
        var size = page.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidDataException("The PDF page dimensions are invalid.");
        }

        var scale = MaxPreviewDimension / Math.Max(size.Width, size.Height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale))
        };

        using var output = new InMemoryRandomAccessStream();
        try
        {
            await page.RenderToStreamAsync(output, options).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (COMException exception)
        {
            throw new InvalidDataException("Windows could not render this PDF page.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Seek(0);
        using var bitmapStream = output.AsStream();
        return DecodeBitmap(bitmapStream);
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        pdfInput?.Dispose();
    }

    private static async Task<DocumentPreviewRenderer> OpenPdfAsync(byte[] content, CancellationToken cancellationToken)
    {
        var input = new MemoryStream(content, writable: false).AsRandomAccessStream();
        try
        {
            var document = await PdfDocument.LoadFromStreamAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (document.PageCount == 0)
            {
                throw new InvalidDataException("The PDF does not contain any pages.");
            }

            // PDF pages continue to read the source stream after the document opens.
            return new DocumentPreviewRenderer(document, input);
        }
        catch (COMException exception)
        {
            input.Dispose();
            throw new InvalidDataException("Windows could not open this PDF.", exception);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static bool IsBitmap(ReadOnlySpan<byte> content, string? mediaType)
    {
        var knownMediaType = mediaType?.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif"
            or "image/bmp" or "image/tiff" or "image/x-icon";
        return knownMediaType ||
            content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8) || content.StartsWith("BM"u8) ||
            content.StartsWith("II*\0"u8) || content.StartsWith("MM\0*"u8) ||
            content.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
            content.StartsWith<byte>([0xFF, 0xD8, 0xFF]) || content.StartsWith<byte>([0x00, 0x00, 0x01, 0x00]);
    }

    private static BitmapSource DecodeImage(byte[] content)
    {
        using var header = new MemoryStream(content, writable: false);
        var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The image does not contain a frame.");
        }

        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;

        using var stream = new MemoryStream(content, writable: false);
        return DecodeBitmap(stream,
            width > MaxPreviewDimension && width >= height ? MaxPreviewDimension : 0,
            height > MaxPreviewDimension && height > width ? MaxPreviewDimension : 0);
    }

    private static BitmapSource DecodeBitmap(Stream stream, int decodeWidth = 0, int decodeHeight = 0)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.DecodePixelHeight = decodeHeight;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
