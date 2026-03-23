using CalendarParse.Services;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;

namespace CalendarParse.Cli.Services
{
    /// <summary>Which pre-processing (if any) to apply before sending an image to the vision model.</summary>
    public enum PreprocessMode
    {
        /// <summary>No preprocessing — raw image bytes passed through unchanged.</summary>
        None,
        /// <summary>Grayscale → GaussianBlur 5×5 → AdaptiveThreshold (BinaryInv) → Dilate 2×2. Legacy OCR pipeline.</summary>
        Current,
        /// <summary>Grayscale → EqualizeHist. Global histogram equalization for contrast normalisation.</summary>
        Clahe,
        /// <summary>Unsharp-mask sharpen on the colour image. Keeps RGB training signal for the LLM.</summary>
        Llm,
        /// <summary>Grayscale → FastNlMeansDenoising → EqualizeHist. For noisy/scan images.</summary>
        Denoise,
    }

    /// <summary>
    /// Windows implementation of <see cref="IImagePreprocessor"/> using Emgu.CV.
    /// Applies grayscale → blur → adaptive threshold → dilation to prepare the image for grid detection.
    /// </summary>
    public class WindowsImagePreprocessor : IImagePreprocessor
    {
        /// <summary>Returns the file extension that matches the bytes produced by <see cref="PreprocessBytes"/>.</summary>
        public static string GetExtension(PreprocessMode mode) =>
            mode is PreprocessMode.None or PreprocessMode.Llm ? ".jpg" : ".png";

        /// <summary>
        /// Synchronously applies the requested preprocessing to raw image bytes.
        /// <see cref="PreprocessMode.None"/> returns <paramref name="raw"/> unchanged (no allocation).
        /// </summary>
        public byte[] PreprocessBytes(byte[] raw, PreprocessMode mode)
        {
            if (mode == PreprocessMode.None) return raw;

            using var src = new Mat();
            CvInvoke.Imdecode(raw, ImreadModes.ColorBgr, src);

            var output = new VectorOfByte();

            switch (mode)
            {
                case PreprocessMode.Current:
                {
                    using var gray    = new Mat();
                    using var blurred = new Mat();
                    using var thresh  = new Mat();
                    using var kernel  = CvInvoke.GetStructuringElement(MorphShapes.Rectangle,
                                            new System.Drawing.Size(2, 2), new System.Drawing.Point(-1, -1));
                    using var dilated = new Mat();
                    CvInvoke.CvtColor(src, gray, ColorConversion.Bgr2Gray);
                    CvInvoke.GaussianBlur(gray, blurred, new System.Drawing.Size(5, 5), 0);
                    CvInvoke.AdaptiveThreshold(blurred, thresh, 255,
                        AdaptiveThresholdType.GaussianC, ThresholdType.BinaryInv, 15, 2);
                    CvInvoke.MorphologyEx(thresh, dilated, MorphOp.Dilate, kernel,
                        new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));
                    CvInvoke.Imencode(".png", dilated, output);
                    break;
                }
                case PreprocessMode.Clahe:
                {
                    using var gray   = new Mat();
                    using var result = new Mat();
                    CvInvoke.CvtColor(src, gray, ColorConversion.Bgr2Gray);
                    CvInvoke.EqualizeHist(gray, result);
                    CvInvoke.Imencode(".png", result, output);
                    break;
                }
                case PreprocessMode.Llm:
                {
                    // Unsharp mask on the colour image: amplify edges, preserve colour signal
                    using var blurred   = new Mat();
                    using var sharpened = new Mat();
                    CvInvoke.GaussianBlur(src, blurred, new System.Drawing.Size(0, 0), 3);
                    CvInvoke.AddWeighted(src, 1.5, blurred, -0.5, 0, sharpened);
                    CvInvoke.Imencode(".jpg", sharpened, output);
                    break;
                }
                case PreprocessMode.Denoise:
                {
                    using var gray     = new Mat();
                    using var denoised = new Mat();
                    using var result   = new Mat();
                    CvInvoke.CvtColor(src, gray, ColorConversion.Bgr2Gray);
                    CvInvoke.FastNlMeansDenoising(gray, denoised, 5.0f, 7, 21);
                    CvInvoke.EqualizeHist(denoised, result);
                    CvInvoke.Imencode(".png", result, output);
                    break;
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Downscales the image to <paramref name="targetWidth"/> pixels wide (preserving aspect ratio)
        /// using area interpolation, which is optimal for downsampling.
        /// If the image is already narrower than <paramref name="targetWidth"/>, the original bytes are
        /// returned unchanged (no upsampling).
        /// </summary>
        public byte[] ResizeToWidth(byte[] raw, int targetWidth)
        {
            using var src = new Mat();
            CvInvoke.Imdecode(raw, ImreadModes.ColorBgr, src);
            if (src.Width <= targetWidth) return raw;

            double scale   = (double)targetWidth / src.Width;
            var    newSize = new System.Drawing.Size(targetWidth, (int)(src.Height * scale));
            using var resized = new Mat();
            CvInvoke.Resize(src, resized, newSize, 0, 0, Inter.Area);

            var output = new VectorOfByte();
            CvInvoke.Imencode(".jpg", resized, output);
            return output.ToArray();
        }

        /// <summary>
        /// Creates a composite image for bottom-employee re-extraction:
        /// takes the top <paramref name="headerFraction"/> of the original (the date header row)
        /// and stacks it above the bottom <c>1 - <paramref name="splitFraction"/></c> of the original
        /// (the lower employee rows). Sending this to the model gives it higher pixel density
        /// on the lower-table employees that are most prone to WRONG-COL errors.
        /// </summary>
        public byte[] CreateHeaderAndBottomHalf(byte[] raw,
            double headerFraction = 0.12,
            double splitFraction  = 0.50)
        {
            using var src = new Mat();
            CvInvoke.Imdecode(raw, ImreadModes.ColorBgr, src);

            int headerH = Math.Max(1, (int)(src.Height * headerFraction));
            int splitY  = Math.Min(src.Height - 1, (int)(src.Height * splitFraction));

            var headerRect = new System.Drawing.Rectangle(0, 0,      src.Width, headerH);
            var bottomRect = new System.Drawing.Rectangle(0, splitY, src.Width, src.Height - splitY);

            using var headerMat = new Mat(src, headerRect);
            using var bottomMat = new Mat(src, bottomRect);

            using var composite = new Mat();
            CvInvoke.VConcat(headerMat, bottomMat, composite);

            var output = new VectorOfByte();
            CvInvoke.Imencode(".jpg", composite, output);
            return output.ToArray();
        }

        public Task<byte[]> PreprocessAsync(Stream imageStream, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                using var ms = new MemoryStream();
                imageStream.CopyTo(ms);
                // Delegate to the named-mode method so the two paths stay in sync
                return PreprocessBytes(ms.ToArray(), PreprocessMode.Current);
            }, ct);
        }

        public Task<byte[]> RotateAsync(byte[] imageBytes, int degreesClockwise, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var src = new Mat();
                CvInvoke.Imdecode(imageBytes, ImreadModes.ColorBgr, src);

                var rotated = new Mat();
                var rotateFlag = degreesClockwise switch
                {
                    90  => RotateFlags.Rotate90Clockwise,
                    180 => RotateFlags.Rotate180,
                    270 => RotateFlags.Rotate90CounterClockwise,
                    _   => throw new ArgumentOutOfRangeException(nameof(degreesClockwise),
                               "Only 90, 180, or 270 degrees are supported.")
                };
                CvInvoke.Rotate(src, rotated, rotateFlag);

                var output = new VectorOfByte();
                CvInvoke.Imencode(".jpg", rotated, output);

                src.Dispose();
                rotated.Dispose();

                return output.ToArray();
            }, ct);
        }
    }
}
