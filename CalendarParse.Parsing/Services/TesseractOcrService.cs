using Tesseract;
using CalendarParse.Services;
using ModelRect = CalendarParse.Models.Rect;

namespace CalendarParse.Parsing.Services
{
    /// <summary>
    /// Cross-platform implementation of <see cref="IOcrService"/> using the Tesseract OCR engine.
    /// Requires a tessdata directory containing eng.traineddata.
    /// Returns per-word confidence scores (0.0–1.0), unlike WinRT OCR which always returns 1.0f.
    /// Uses <see cref="PageSegMode.SparseText"/> to skip layout analysis (better for dense grids)
    /// and sets a 300 DPI hint so Tesseract sizes characters correctly for phone photos.
    /// </summary>
    public class TesseractOcrService : IOcrService
    {
        private readonly string _tessDataPath;

        /// <param name="tessDataPath">
        /// Absolute path to the tessdata directory (the folder containing eng.traineddata).
        /// Typically <c>Path.Combine(AppContext.BaseDirectory, "tessdata")</c>.
        /// </param>
        public TesseractOcrService(string tessDataPath)
        {
            _tessDataPath = tessDataPath;
        }

        // Upscale factor applied to the image before OCR.
        // Phone photos at ~150 effective DPI → Tesseract can't find time-range
        // header tokens needed for strip-mode column detection. 2× brings them
        // to ~300 DPI, matching LSTM training expectations. Bounding boxes are
        // divided by this factor before returning so callers get original-image coords.
        private const float UpscaleFactor = 2.0f;

        public Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            var elements = new List<OcrElement>();

            using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
            // Whitelist: only chars that appear in time ranges, names, dates, and shift codes
            engine.SetVariable("tessedit_char_whitelist",
                "0123456789:- ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz/.");

            // Upscale before OCR so character height sits within LSTM's trained range.
            using var rawPix = Pix.LoadFromMemory(imageBytes);
            using var img    = rawPix.Scale(UpscaleFactor, UpscaleFactor);
            // DPI hint reflects the upscaled resolution (original ~150 × 2 ≈ 300 DPI).
            img.XRes = 300;
            img.YRes = 300;
            // SparseText skips Tesseract's layout analysis (column/paragraph detection).
            // Calendar grids confuse the auto-segmenter; sparse mode finds every word independently.
            using var page   = engine.Process(img, PageSegMode.SparseText);

            using var iter = page.GetIterator();
            iter.Begin();

            do
            {
                if (ct.IsCancellationRequested) break;

                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                {
                    var text = iter.GetText(PageIteratorLevel.Word)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Scale bounding box back to original image coordinates.
                        elements.Add(new OcrElement
                        {
                            Text       = text,
                            Confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100f,
                            Bounds     = new ModelRect(
                                (int)(bounds.X1 / UpscaleFactor),
                                (int)(bounds.Y1 / UpscaleFactor),
                                (int)((bounds.X2 - bounds.X1) / UpscaleFactor),
                                (int)((bounds.Y2 - bounds.Y1) / UpscaleFactor))
                        });
                    }
                }
            }
            while (iter.Next(PageIteratorLevel.Word));

            return Task.FromResult(elements);
        }
    }
}
