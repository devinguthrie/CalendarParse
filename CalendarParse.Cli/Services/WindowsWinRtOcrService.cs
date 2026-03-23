using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CalendarParse.Services;
using ModelRect = CalendarParse.Models.Rect;

namespace CalendarParse.Cli.Services
{
    /// <summary>
    /// Windows implementation of <see cref="IOcrService"/> using the built-in
    /// Windows.Media.Ocr engine (Windows Runtime OCR).
    /// No external tessdata required. Returns confidence=1.0f for all words
    /// since WinRT OCR does not expose per-word confidence scores.
    /// </summary>
    public class WindowsWinRtOcrService : IOcrService
    {
        private readonly OcrEngine _engine;

        public WindowsWinRtOcrService()
        {
            _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                   ?? OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? throw new InvalidOperationException(
                          "No English OCR language pack available. " +
                          "Install the English language pack via Windows Settings > Time & Language.");
        }

        public async Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            using var stream = new InMemoryRandomAccessStream();

            // Write bytes into the WinRT stream then rewind
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync().AsTask(ct);
                writer.DetachStream(); // prevent DataWriter.Dispose from closing the stream
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

            // Scale down if image exceeds the engine's maximum dimension (4096 px)
            SoftwareBitmap bitmap;
            uint maxDim = OcrEngine.MaxImageDimension;
            if (decoder.PixelWidth > maxDim || decoder.PixelHeight > maxDim)
            {
                double scale = Math.Min((double)maxDim / decoder.PixelWidth,
                                        (double)maxDim / decoder.PixelHeight);
                var transform = new BitmapTransform
                {
                    ScaledWidth  = (uint)(decoder.PixelWidth  * scale),
                    ScaledHeight = (uint)(decoder.PixelHeight * scale),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(ct);
            }
            else
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
            }

            OcrResult result;
            using (bitmap)
                result = await _engine.RecognizeAsync(bitmap).AsTask(ct);

            var elements = new List<OcrElement>();
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var text = word.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    elements.Add(new OcrElement
                    {
                        Text = text,
                        Confidence = 1.0f, // WinRT OCR has no per-word confidence score
                        Bounds = new ModelRect(
                            (int)word.BoundingRect.X,
                            (int)word.BoundingRect.Y,
                            (int)word.BoundingRect.Width,
                            (int)word.BoundingRect.Height)
                    });
                }
            }
            return elements;
        }
    }
}
