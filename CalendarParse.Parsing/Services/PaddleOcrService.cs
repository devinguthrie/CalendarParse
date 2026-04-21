using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalendarParse.Services;
using ModelRect = CalendarParse.Models.Rect;

namespace CalendarParse.Parsing.Services
{
    /// <summary>
    /// <see cref="IOcrService"/> implementation that delegates to PaddleOCR via a persistent
    /// Python subprocess. The subprocess keeps the model loaded across calls, so cold-start
    /// overhead (~10 s) is paid only once per benchmark run.
    ///
    /// Protocol (line-based, UTF-8):
    ///   Startup  → subprocess prints "READY" when the model is loaded.
    ///   Request  → C# sends an absolute image-file path on stdin.
    ///   Response → subprocess prints one JSON line with the OCR results.
    ///   Shutdown → C# sends "QUIT" (or closes stdin) to exit.
    /// </summary>
    public sealed class PaddleOcrService : IOcrService, IDisposable
    {
        // Same character set as TesseractOcrService's whitelist — keeps comparison fair.
        private static readonly Regex WhitelistRegex =
            new(@"^[0-9:\- A-Za-z/.]+$", RegexOptions.Compiled);

        private readonly Process      _process;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private bool _disposed;

        /// <param name="scriptPath">Absolute path to <c>paddleocr_ocr.py</c>.</param>
        /// <param name="pythonExe">
        /// Python executable to use. Pass the full venv path when the system PATH
        /// does not include an environment with PaddleOCR installed.
        /// </param>
        public PaddleOcrService(string scriptPath, string pythonExe = "python")
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                Arguments              = $"-u \"{scriptPath}\"",  // -u = unbuffered stdout
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = false,
                CreateNoWindow         = true,
            };

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PaddleOCR Python subprocess.");

            _stdin  = _process.StandardInput;
            _stdout = _process.StandardOutput;

            // Block until the model is fully loaded.
            var ready = _stdout.ReadLine();
            if (ready != "READY")
                throw new InvalidOperationException(
                    $"PaddleOCR subprocess did not signal readiness. First line was: {ready}");
        }

        public async Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
        {
            // PaddleOCR requires a file path, not raw bytes.
            var tmpPath = Path.Combine(Path.GetTempPath(), $"paddleocr_{Guid.NewGuid():N}.jpg");
            try
            {
                await File.WriteAllBytesAsync(tmpPath, imageBytes, ct).ConfigureAwait(false);

                await _stdin.WriteLineAsync(tmpPath).ConfigureAwait(false);
                await _stdin.FlushAsync(ct).ConfigureAwait(false);

                var jsonLine = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(jsonLine))
                    return [];

                using var doc      = JsonDocument.Parse(jsonLine);
                var       elements = new List<OcrElement>();

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var text = item.GetProperty("text").GetString() ?? string.Empty;

                    // Drop tokens that contain chars outside the Tesseract whitelist set.
                    // This prevents OCR noise from grid lines / borders poisoning column detection.
                    if (!WhitelistRegex.IsMatch(text))
                        continue;

                    elements.Add(new OcrElement
                    {
                        Text       = text,
                        Confidence = item.GetProperty("confidence").GetSingle(),
                        Bounds     = new ModelRect(
                            item.GetProperty("x").GetInt32(),
                            item.GetProperty("y").GetInt32(),
                            item.GetProperty("width").GetInt32(),
                            item.GetProperty("height").GetInt32()),
                    });
                }

                return elements;
            }
            finally
            {
                try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _stdin.WriteLine("QUIT");
                _stdin.Flush();
            }
            catch { /* subprocess may already be dead */ }
            finally
            {
                _stdin.Dispose();
                _stdout.Dispose();
                _process.Dispose();
            }
        }
    }
}
