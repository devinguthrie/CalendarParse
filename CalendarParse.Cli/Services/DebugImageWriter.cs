using CalendarParse.Models;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using System.Drawing;

namespace CalendarParse.Cli.Services
{
    /// <summary>
    /// Saves annotated debug images at each stage of the table parsing pipeline.
    /// All methods write a PNG file to the specified path, creating parent directories as needed.
    /// </summary>
    public static class DebugImageWriter
    {
        // ── Color palette (BGR) ───────────────────────────────────────────────
        private static readonly MCvScalar ColorGreen    = new(0,   200,   0);   // detected cell
        private static readonly MCvScalar ColorBlue     = new(220,  80,   0);   // header row
        private static readonly MCvScalar ColorYellow   = new(0,   200, 220);   // name column
        private static readonly MCvScalar ColorCyan     = new(200, 200,   0);   // shift cell
        private static readonly MCvScalar ColorMagenta  = new(180,   0, 180);   // hours cell
        private static readonly MCvScalar ColorRed      = new(0,    0,  220);   // OCR text
        private static readonly MCvScalar ColorWhite    = new(255, 255, 255);

        /// <summary>Step 1 — Save raw source image as-is.</summary>
        public static void SaveRaw(byte[] imageBytes, string path)
        {
            EnsureDir(path);
            File.WriteAllBytes(path, imageBytes);
        }

        /// <summary>Step 2 — Save the adaptive-threshold preprocessed PNG side-by-side or alone.</summary>
        public static void SavePreprocessed(byte[] preprocessedPng, string path)
        {
            EnsureDir(path);
            File.WriteAllBytes(path, preprocessedPng);
        }

        /// <summary>
        /// Step 3 — Draw all detected cell bounding boxes on the original image.
        /// Cells are colour-coded by inferred role:
        ///   Blue   = header row
        ///   Yellow = name column
        ///   Cyan   = shift-time data cell  (odd data column after the name col)
        ///   Magenta= hours-worked data cell (even data column after the name col)
        ///   Green  = everything else
        /// Row,Col label is printed in the top-left of each cell.
        /// </summary>
        public static void SaveCellsOverlay(
            byte[] originalImage,
            List<TableCell> cells,
            string path,
            int headerRow = -1,
            int nameCol = 0)
        {
            EnsureDir(path);

            var src = DecodeColor(originalImage);

            foreach (var cell in cells)
            {
                var color = CellColor(cell, headerRow, nameCol);
                var rect  = ToDrawingRect(cell.Bounds);
                CvInvoke.Rectangle(src, rect, color, 2);

                // Row,Col label
                string label = $"{cell.Row},{cell.Col}";
                CvInvoke.PutText(src, label,
                    new Point(cell.Bounds.X + 3, cell.Bounds.Y + 14),
                    FontFace.HersheyPlain, 0.85, color, 1);
            }

            // Legend in upper-left
            DrawLegend(src);

            SavePng(src, path);
        }

        /// <summary>
        /// Step 4 — Draw OCR-recognised text inside each cell on the original image.
        /// Empty cells still get a thin green outline; non-empty cells get a coloured box + text.
        /// </summary>
        public static void SaveOcrOverlay(
            byte[] originalImage,
            List<TableCell> cells,
            string path,
            int headerRow = -1,
            int nameCol = 0)
        {
            EnsureDir(path);

            var src = DecodeColor(originalImage);

            foreach (var cell in cells)
            {
                var rect = ToDrawingRect(cell.Bounds);

                if (string.IsNullOrWhiteSpace(cell.Text))
                {
                    CvInvoke.Rectangle(src, rect, ColorGreen, 1);
                    continue;
                }

                var color = CellColor(cell, headerRow, nameCol);
                CvInvoke.Rectangle(src, rect, color, 2);

                // Semi-transparent background behind text for readability
                var textPt = new Point(cell.Bounds.X + 3, cell.Bounds.Y + cell.Bounds.Height / 2 + 5);

                // Scale font to fit cell width
                double fontScale = Math.Clamp(cell.Bounds.Width / 80.0, 0.55, 1.0);
                CvInvoke.PutText(src, cell.Text, textPt,
                    FontFace.HersheyPlain, fontScale, new MCvScalar(0, 0, 0), 3); // shadow
                CvInvoke.PutText(src, cell.Text, textPt,
                    FontFace.HersheyPlain, fontScale, ColorWhite, 1);             // text
            }

            SavePng(src, path);
        }

        /// <summary>
        /// Step 3b — Overlay the detected horizontal + vertical line masks as coloured channels
        /// on top of the original image so you can see what the morphology picked up.
        /// Horizontal lines tinted blue, vertical lines tinted red, both blended at ~50% opacity.
        /// </summary>
        public static void SaveGridLinesOverlay(
            byte[] originalImage,
            byte[] hLinesPng,
            byte[] vLinesPng,
            string path)
        {
            EnsureDir(path);

            var src = DecodeColor(originalImage);

            // Decode grayscale masks
            var hGray = new Mat();
            CvInvoke.Imdecode(hLinesPng, ImreadModes.Grayscale, hGray);
            var vGray = new Mat();
            CvInvoke.Imdecode(vLinesPng, ImreadModes.Grayscale, vGray);

            // Build BGR colour planes for each mask
            // Horizontal lines → pure blue  (B=255, G=0,   R=0)
            // Vertical lines   → pure red   (B=0,   G=0,   R=255)
            var zero = new Mat(src.Size, DepthType.Cv8U, 1);
            zero.SetTo(new MCvScalar(0));

            // hColour = merge(hGray, zero, zero)   → blue
            var hColour = new Mat();
            CvInvoke.Merge(new VectorOfMat(hGray, zero, zero), hColour);

            // vColour = merge(zero, zero, vGray)   → red
            var vColour = new Mat();
            CvInvoke.Merge(new VectorOfMat(zero, zero, vGray), vColour);

            // Blend: dst = src*0.55 + hColour*0.45
            var blended = new Mat();
            CvInvoke.AddWeighted(src, 0.55, hColour, 0.45, 0, blended);

            // Blend vertical lines on top
            var final = new Mat();
            CvInvoke.AddWeighted(blended, 0.55, vColour, 0.45, 0, final);

            hGray.Dispose(); vGray.Dispose(); zero.Dispose();
            hColour.Dispose(); vColour.Dispose(); blended.Dispose();
            src.Dispose();

            SavePng(final, path);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static Mat DecodeColor(byte[] imageBytes)
        {
            var mat = new Mat();
            CvInvoke.Imdecode(imageBytes, ImreadModes.ColorBgr, mat);
            return mat;
        }

        private static void SavePng(Mat mat, string path)
        {
            var buf = new VectorOfByte();
            CvInvoke.Imencode(".png", mat, buf);
            File.WriteAllBytes(path, buf.ToArray());
            mat.Dispose();
        }

        private static Rectangle ToDrawingRect(Rect r) =>
            new(r.X, r.Y, r.Width, r.Height);

        private static MCvScalar CellColor(TableCell cell, int headerRow, int nameCol)
        {
            if (cell.Row == headerRow)    return ColorBlue;
            if (cell.Col == nameCol)      return ColorYellow;

            int dataOffset = cell.Col - (nameCol + 1);
            if (dataOffset < 0)           return ColorGreen;
            return dataOffset % 2 == 0 ? ColorCyan : ColorMagenta;
        }

        /// <summary>Draws a small role legend in the top-left corner of the Mat.</summary>
        private static void DrawLegend(Mat mat)
        {
            var entries = new (MCvScalar color, string label)[]
            {
                (ColorBlue,    "Header row"),
                (ColorYellow,  "Name col"),
                (ColorCyan,    "Shift cell"),
                (ColorMagenta, "Hours cell"),
                (ColorGreen,   "Other"),
            };

            int x = 8, y = 20;
            foreach (var (color, label) in entries)
            {
                CvInvoke.Rectangle(mat, new Rectangle(x, y - 10, 14, 14), color, -1);
                CvInvoke.PutText(mat, label, new Point(x + 18, y), FontFace.HersheyPlain, 0.9,
                    new MCvScalar(0, 0, 0), 2);
                CvInvoke.PutText(mat, label, new Point(x + 18, y), FontFace.HersheyPlain, 0.9,
                    ColorWhite, 1);
                y += 18;
            }
        }

        private static void EnsureDir(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
