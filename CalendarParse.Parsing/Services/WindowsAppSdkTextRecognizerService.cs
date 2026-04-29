#if WINDOWS
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Microsoft.Graphics.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using CalendarParse.Services;
using ModelRect = CalendarParse.Models.Rect;

namespace CalendarParse.Parsing.Services
{
    /// <summary>
    /// IOcrService implementation using the Windows App SDK AI TextRecognizer
    /// (Microsoft.Windows.AI.Imaging.TextRecognizer).
    ///
    /// Key differences from WindowsWinRtOcrService (Windows.Media.Ocr):
    ///   - AI-based, NPU-accelerated for higher accuracy on printed/handwritten text
    ///   - Returns real per-word Confidence scores (0.0–1.0)
    ///   - Returns polygon BoundingBox (4 corners) instead of rectangular BoundingRect
    ///
    /// Requires Microsoft.WindowsAppSDK.AI 1.8+ and the Windows AI model component
    /// (installed via EnsureReadyAsync). If the model is unavailable (e.g., no NPU),
    /// initialization logs a warning and RecognizeAsync returns an empty list.
    /// </summary>
    public sealed class WindowsAppSdkTextRecognizerService : IOcrService
    {
        private TextRecognizer? _recognizer;
        private bool _initialized;

        public async Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            if (!_initialized)
                await EnsureInitializedAsync(ct);

            if (_recognizer is null)
                return [];

            // Decode image bytes → SoftwareBitmap using same WinRT pipeline as WinRtOcrService
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync().AsTask(ct);
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
            var bitmap  = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);

            // Run AI recognition on a background thread (synchronous call)
            using (bitmap)
            {
                var recognizer = _recognizer; // capture for lambda
                return await Task.Run(() => RecognizeFromBitmap(recognizer, bitmap), ct);
            }
        }

        private static List<OcrElement> RecognizeFromBitmap(TextRecognizer recognizer, SoftwareBitmap bitmap)
        {
            var imageBuffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);
            var result      = recognizer.RecognizeTextFromImage(imageBuffer);
            var elements    = new List<OcrElement>();

            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var text = word.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // Compute axis-aligned bounding rect from the polygon's four corners
                    var bb  = word.BoundingBox;
                    float x0 = (float)Math.Min(Math.Min(bb.TopLeft.X, bb.TopRight.X),
                                               Math.Min(bb.BottomLeft.X, bb.BottomRight.X));
                    float y0 = (float)Math.Min(Math.Min(bb.TopLeft.Y, bb.TopRight.Y),
                                               Math.Min(bb.BottomLeft.Y, bb.BottomRight.Y));
                    float x1 = (float)Math.Max(Math.Max(bb.TopLeft.X, bb.TopRight.X),
                                               Math.Max(bb.BottomLeft.X, bb.BottomRight.X));
                    float y1 = (float)Math.Max(Math.Max(bb.TopLeft.Y, bb.TopRight.Y),
                                               Math.Max(bb.BottomLeft.Y, bb.BottomRight.Y));

                    elements.Add(new OcrElement
                    {
                        Text       = text,
                        Confidence = word.MatchConfidence,
                        Bounds     = new ModelRect((int)x0, (int)y0,
                                                   (int)(x1 - x0), (int)(y1 - y0))
                    });
                }
            }
            return elements;
        }

        private async Task EnsureInitializedAsync(CancellationToken ct)
        {
            _initialized = true; // set before await so concurrent callers don't double-init
            try
            {
                AIFeatureReadyState state;
                try { state = TextRecognizer.GetReadyState(); }
                catch (Exception ex)
                {
                    // E_ACCESSDENIED (0x80070005) = hardware/package-identity requirements not met
                    Console.WriteLine($"[AppSDK-OCR] Not available on this device ({ex.HResult:X8}) — x-ref will be skipped.");
                    return;
                }

                if (state == AIFeatureReadyState.NotReady)
                {
                    Console.WriteLine("[AppSDK-OCR] AI model not installed; installing now...");
                    var loadResult = await TextRecognizer.EnsureReadyAsync().AsTask(ct);
                    if (loadResult.Status != AIFeatureReadyResultState.Success)
                    {
                        Console.WriteLine($"[AppSDK-OCR] Model install failed (status={loadResult.Status}). " +
                                          "X-mark cross-reference will be skipped.");
                        return;
                    }
                    Console.WriteLine("[AppSDK-OCR] Model installed successfully.");
                }
                _recognizer = await TextRecognizer.CreateAsync().AsTask(ct);
                Console.WriteLine("[AppSDK-OCR] TextRecognizer ready.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AppSDK-OCR] Initialization error: {ex.Message} — x-ref will be skipped.");
            }
        }
    }
}
#endif // WINDOWS
