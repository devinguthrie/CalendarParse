using CalendarParse.Services;
using Tesseract;
using ModelRect = CalendarParse.Models.Rect;

namespace CalendarParse.Cli.Services
{
    /// <summary>
    /// Windows implementation of <see cref="IOcrService"/> using Tesseract OCR.
    /// Requires tessdata language files alongside the executable (e.g. tessdata/eng.traineddata).
    /// </summary>
    public class WindowsOcrService : IOcrService
    {
        private readonly string _tessDataPath;

        public WindowsOcrService(string? tessDataPath = null)
        {
            // Default: tessdata/ folder next to the executable
            _tessDataPath = tessDataPath
                ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        }

        public Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                if (!Directory.Exists(_tessDataPath))
                    throw new DirectoryNotFoundException(
                        $"Tesseract data directory not found: {_tessDataPath}\n" +
                        $"Download eng.traineddata from https://github.com/tesseract-ocr/tessdata and place it in that folder.");

                var elements = new List<OcrElement>();

                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var pix = Pix.LoadFromMemory(imageBytes);
                using var page = engine.Process(pix);
                using var iter = page.GetIterator();

                iter.Begin();
                do
                {
                    ct.ThrowIfCancellationRequested();

                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                        continue;

                    var text = iter.GetText(PageIteratorLevel.Word)?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    float confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100f;

                    elements.Add(new OcrElement
                    {
                        Text = text,
                        Confidence = confidence,
                        Bounds = new ModelRect(bounds.X1, bounds.Y1, bounds.Width, bounds.Height)
                    });
                }
                while (iter.Next(PageIteratorLevel.Word));

                return elements;
            }, ct);
        }
    }
}
