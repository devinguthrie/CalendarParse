using CalendarParse.Models;
using CalendarParse.Services;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Rect = CalendarParse.Models.Rect;

namespace CalendarParse.Cli.Services
{
    /// <summary>
    /// Windows implementation of <see cref="ITableDetector"/> using Emgu.CV morphological operations.
    /// </summary>
    public class WindowsTableDetector : ITableDetector
    {
        /// <inheritdoc/>
        public Task<List<TableCell>> DetectCellsAsync(byte[] preprocessedPng, CancellationToken ct = default)
            => Task.Run(() => DetectCore(preprocessedPng, ct).cells, ct);

        /// <summary>
        /// Extended overload that also returns the raw horizontal and vertical line masks as PNG bytes
        /// so they can be used for debug visualisation.
        /// </summary>
        public Task<(List<TableCell> cells, byte[] hLinesPng, byte[] vLinesPng)>
            DetectCellsWithMasksAsync(byte[] preprocessedPng, CancellationToken ct = default)
            => Task.Run(() => DetectCore(preprocessedPng, ct), ct);

        private static (List<TableCell> cells, byte[] hLinesPng, byte[] vLinesPng)
            DetectCore(byte[] preprocessedPng, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var src = new Mat();
            CvInvoke.Imdecode(preprocessedPng, ImreadModes.Grayscale, src);

            int rows = src.Rows;
            int cols = src.Cols;

            // ── Binarize: dark grid lines → white, background → black ─────────
            // Slight blur first to reduce noise from phone-photo images.
            // BinaryInv: dark pixels (lines/borders) → 255, light background → 0.
            var blurred = new Mat();
            CvInvoke.GaussianBlur(src, blurred, new System.Drawing.Size(3, 3), 0);
            var binary = new Mat();
            CvInvoke.AdaptiveThreshold(blurred, binary, 255,
                AdaptiveThresholdType.GaussianC, ThresholdType.BinaryInv, 15, 2);
            blurred.Dispose();

            // ── Horizontal lines ─────────────────────────────────────────────
            int hLen = Math.Max(cols / 25, 15);
            var hKernel = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle,
                new System.Drawing.Size(hLen, 1),
                new System.Drawing.Point(-1, -1));
            var horizontal = new Mat();
            CvInvoke.MorphologyEx(binary, horizontal, MorphOp.Erode, hKernel,
                new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));
            CvInvoke.MorphologyEx(horizontal, horizontal, MorphOp.Dilate, hKernel,
                new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));

            // ── Vertical lines ────────────────────────────────────────────────
            int vLen = Math.Max(rows / 25, 15);
            var vKernel = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle,
                new System.Drawing.Size(1, vLen),
                new System.Drawing.Point(-1, -1));
            var vertical = new Mat();
            CvInvoke.MorphologyEx(binary, vertical, MorphOp.Erode, vKernel,
                new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));
            CvInvoke.MorphologyEx(vertical, vertical, MorphOp.Dilate, vKernel,
                new System.Drawing.Point(-1, -1), 1, BorderType.Default, new MCvScalar(0));

            // ── Diagnostics ───────────────────────────────────────────────────
            {
                int binaryNz = CvInvoke.CountNonZero(binary);
                int hNz      = CvInvoke.CountNonZero(horizontal);
                int vNz      = CvInvoke.CountNonZero(vertical);
                Console.Error.WriteLine(
                    $"      [grid-diag] img={cols}×{rows}  hLen={hLen}  vLen={vLen}" +
                    $"  binary_nz={binaryNz}  h_nz={hNz}  v_nz={vNz}");
            }

            // ── Capture masks as PNG bytes for debug output ───────────────────
            var hBuf = new VectorOfByte();
            CvInvoke.Imencode(".png", horizontal, hBuf);
            byte[] hLinesPng = hBuf.ToArray();

            var vBuf = new VectorOfByte();
            CvInvoke.Imencode(".png", vertical, vBuf);
            byte[] vLinesPng = vBuf.ToArray();

            // ── Combine grid and close small gaps ─────────────────────────────
            var grid = new Mat();
            CvInvoke.Add(horizontal, vertical, grid);

            var closeKernel = CvInvoke.GetStructuringElement(
                MorphShapes.Rectangle,
                new System.Drawing.Size(3, 3),
                new System.Drawing.Point(-1, -1));
            CvInvoke.MorphologyEx(grid, grid, MorphOp.Dilate, closeKernel,
                new System.Drawing.Point(-1, -1), 2, BorderType.Default, new MCvScalar(0));

            // ── Find cell contours (invert: cells become white regions) ────────
            var inverted = new Mat();
            CvInvoke.BitwiseNot(grid, inverted);

            var contours = new VectorOfVectorOfPoint();
            var hierarchy = new Mat();
            CvInvoke.FindContours(inverted, contours, hierarchy,
                RetrType.External, ChainApproxMethod.ChainApproxSimple);

            int minArea = (rows * cols) / 600;
            var rawCells = new List<TableCell>();

            for (int i = 0; i < contours.Size; i++)
            {
                var rect = CvInvoke.BoundingRectangle(contours[i]);
                int area = rect.Width * rect.Height;
                if (area < minArea) continue;
                if (rect.Width > cols * 0.95 || rect.Height > rows * 0.95) continue;

                rawCells.Add(new TableCell
                {
                    Bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height)
                });
            }

            src.Dispose(); binary.Dispose(); horizontal.Dispose(); vertical.Dispose();
            grid.Dispose(); inverted.Dispose(); closeKernel.Dispose();
            hKernel.Dispose(); vKernel.Dispose(); hierarchy.Dispose();

            return (AssignRowCol(rawCells), hLinesPng, vLinesPng);
        }

        private static List<TableCell> AssignRowCol(List<TableCell> cells)
        {
            if (cells.Count == 0) return cells;

            cells.Sort((a, b) => a.Bounds.CenterY.CompareTo(b.Bounds.CenterY));

            int avgHeight = cells.Sum(c => c.Bounds.Height) / cells.Count;
            int tolerance = Math.Max(avgHeight / 2, 10);

            var rows = new List<List<TableCell>>();
            List<TableCell>? currentRow = null;
            int prevCenterY = int.MinValue;

            foreach (var cell in cells)
            {
                if (currentRow == null || Math.Abs(cell.Bounds.CenterY - prevCenterY) > tolerance)
                {
                    currentRow = new List<TableCell>();
                    rows.Add(currentRow);
                    prevCenterY = cell.Bounds.CenterY;
                }
                currentRow.Add(cell);
                prevCenterY = (prevCenterY + cell.Bounds.CenterY) / 2;
            }

            var result = new List<TableCell>();
            for (int r = 0; r < rows.Count; r++)
            {
                var sortedRow = rows[r].OrderBy(c => c.Bounds.X).ToList();
                for (int c = 0; c < sortedRow.Count; c++)
                {
                    sortedRow[c].Row = r;
                    sortedRow[c].Col = c;
                    result.Add(sortedRow[c]);
                }
            }
            return result;
        }
    }
}
