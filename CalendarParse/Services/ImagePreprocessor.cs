using CalendarParse.Models;

#if ANDROID
using Android.Util;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
#endif

namespace CalendarParse.Services
{
    public class ImagePreprocessor : IImagePreprocessor
    {
        public Task<byte[]> PreprocessAsync(Stream imageStream, CancellationToken ct = default)
        {
#if ANDROID
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var rawBytes = ReadAllBytes(imageStream);

                // Decode image
                var src = new Mat();
                CvInvoke.Imdecode(rawBytes, ImreadModes.ColorBgr, src);
                Log.Debug("CalParse.Prep", $"Raw bytes={rawBytes.Length}  decoded={src.Cols}x{src.Rows}");

                // Convert to grayscale
                var gray = new Mat();
                CvInvoke.CvtColor(src, gray, ColorConversion.Bgr2Gray);

                // Gaussian blur to reduce noise
                var blurred = new Mat();
                CvInvoke.GaussianBlur(gray, blurred, new System.Drawing.Size(5, 5), 0);

                // Adaptive threshold: grid lines become white on black background
                var thresh = new Mat();
                CvInvoke.AdaptiveThreshold(blurred, thresh, 255,
                    AdaptiveThresholdType.GaussianC, ThresholdType.BinaryInv, 15, 2);

                // Dilate slightly to connect broken lines
                var kernel = CvInvoke.GetStructuringElement(
                    MorphShapes.Rectangle,
                    new System.Drawing.Size(2, 2),
                    new System.Drawing.Point(-1, -1));
                var dilated = new Mat();
                CvInvoke.MorphologyEx(thresh, dilated, MorphOp.Dilate, kernel,
                    new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));

                var output = new VectorOfByte();
                CvInvoke.Imencode(".png", dilated, output);

                src.Dispose(); gray.Dispose(); blurred.Dispose();
                thresh.Dispose(); dilated.Dispose(); kernel.Dispose();

                var result = output.ToArray();
                Log.Debug("CalParse.Prep", $"Preprocessed PNG size: {result.Length} bytes");
                return result;
            }, ct);
#else
            throw new PlatformNotSupportedException("Image preprocessing is only supported on Android.");
#endif
        }

        // Android photos are already correctly oriented by the camera/EXIF handling,
        // so rotation is a no-op here.
        public Task<byte[]> RotateAsync(byte[] imageBytes, int degreesClockwise, CancellationToken ct = default)
            => Task.FromResult(imageBytes);

        private static byte[] ReadAllBytes(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
