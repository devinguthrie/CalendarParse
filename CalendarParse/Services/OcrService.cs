using CalendarParse.Models;
using ModelRect = CalendarParse.Models.Rect;

#if ANDROID
using Android.Graphics;
using Android.Util;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
#endif

namespace CalendarParse.Services
{
    /// <summary>
    /// Wraps Google ML Kit Text Recognition V2 for Android.
    /// Returns OCR elements with bounding boxes in image-pixel coordinates.
    /// </summary>
    public class OcrService : IOcrService
    {
        public async Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
#if ANDROID
            var bitmap = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length)
                ?? throw new InvalidOperationException("Failed to decode image bytes for OCR.");

            var inputImage = InputImage.FromBitmap(bitmap, 0);
            var recognizer = TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);

            // Process returns Java Task; result type is the concrete Text class
            var javaResult = await recognizer.Process(inputImage).AsTaskAsync<Java.Lang.Object>(ct);
            var textResult = (Text)(object)javaResult;

            var elements = new List<OcrElement>();
            var textBlocks = textResult.TextBlocks;

            Log.Debug("CalParse.OCR", $"Image size: {bitmap.Width}x{bitmap.Height}  |  TextBlocks: {textBlocks?.Count ?? 0}");

            if (textBlocks is null)
            {
                bitmap.Recycle();
                recognizer.Close();
                return elements;
            }

            foreach (var block in textBlocks)
            {
                if (block?.Lines is null) continue;
                foreach (var line in block.Lines)
                {
                    if (line?.Elements is null) continue;
                    foreach (var element in line.Elements)
                    {
                        var bounds = element?.BoundingBox;
                        if (element is null || bounds is null) continue;

                        elements.Add(new OcrElement
                        {
                            Text = element.Text ?? string.Empty,
                            Confidence = element.Confidence,
                            Bounds = new ModelRect(bounds.Left, bounds.Top, bounds.Width(), bounds.Height())
                        });
                    }
                }
            }

            Log.Debug("CalParse.OCR", $"Total OCR elements: {elements.Count}");
            for (int i = 0; i < Math.Min(elements.Count, 60); i++)
            {
                var e = elements[i];
                Log.Debug("CalParse.OCR", $"  [{i:D3}] \"{e.Text}\" conf={e.Confidence:F2} bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})");
            }
            if (elements.Count > 60)
                Log.Debug("CalParse.OCR", $"  ... ({elements.Count - 60} more elements not shown)");

            bitmap.Recycle();
            recognizer.Close();
            return elements;
#else
            await Task.CompletedTask;
            return new List<OcrElement>();
#endif
        }
    }
}
