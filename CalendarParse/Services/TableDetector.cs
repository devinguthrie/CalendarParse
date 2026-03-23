using CalendarParse.Models;
using Rect = CalendarParse.Models.Rect;

#if ANDROID
using Android.Util;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
#endif

namespace CalendarParse.Services
{
    /// <summary>
    /// Detects the table grid in a preprocessed (binary) image using OpenCV morphological operations.
    /// Strategy:
    ///   1. Isolate horizontal lines using a wide horizontal erosion kernel.
    ///   2. Isolate vertical lines using a tall vertical erosion kernel.
    ///   3. Combine → find cell contours → sort into row/col matrix.
    /// </summary>
    public class TableDetector : ITableDetector
    {
        public Task<List<TableCell>> DetectCellsAsync(byte[] preprocessedPng, CancellationToken ct = default)
        {
#if ANDROID
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var src = new Mat();
                CvInvoke.Imdecode(preprocessedPng, ImreadModes.Grayscale, src);

                int rows = src.Rows;
                int cols = src.Cols;
                Log.Debug("CalParse.Table", $"Preprocessed image: {cols}x{rows}");

                // ── Horizontal lines ─────────────────────────────────────
                int hLen = Math.Max(cols / 25, 15);
                var hKernel = CvInvoke.GetStructuringElement(
                    MorphShapes.Rectangle,
                    new System.Drawing.Size(hLen, 1),
                    new System.Drawing.Point(-1, -1));
                var horizontal = new Mat();
                CvInvoke.MorphologyEx(src, horizontal, MorphOp.Erode, hKernel,
                    new System.Drawing.Point(-1, -1), 3, BorderType.Default, new MCvScalar(0));
                CvInvoke.MorphologyEx(horizontal, horizontal, MorphOp.Dilate, hKernel,
                    new System.Drawing.Point(-1, -1), 3, BorderType.Default, new MCvScalar(0));

                // ── Vertical lines ────────────────────────────────────────
                int vLen = Math.Max(rows / 25, 15);
                var vKernel = CvInvoke.GetStructuringElement(
                    MorphShapes.Rectangle,
                    new System.Drawing.Size(1, vLen),
                    new System.Drawing.Point(-1, -1));
                var vertical = new Mat();
                CvInvoke.MorphologyEx(src, vertical, MorphOp.Erode, vKernel,
                    new System.Drawing.Point(-1, -1), 3, BorderType.Default, new MCvScalar(0));
                CvInvoke.MorphologyEx(vertical, vertical, MorphOp.Dilate, vKernel,
                    new System.Drawing.Point(-1, -1), 3, BorderType.Default, new MCvScalar(0));

                // ── Combine grid and close small gaps ─────────────────────
                var grid = new Mat();
                CvInvoke.Add(horizontal, vertical, grid);

                var closeKernel = CvInvoke.GetStructuringElement(
                    MorphShapes.Rectangle,
                    new System.Drawing.Size(3, 3),
                    new System.Drawing.Point(-1, -1));
                CvInvoke.MorphologyEx(grid, grid, MorphOp.Dilate, closeKernel,
                    new System.Drawing.Point(-1, -1), 2, BorderType.Default, new MCvScalar(0));

                // ── Find cell contours (invert: cells become white regions) ──
                var inverted = new Mat();
                CvInvoke.BitwiseNot(grid, inverted);

                var contours = new VectorOfVectorOfPoint();
                var hierarchy = new Mat();
                CvInvoke.FindContours(inverted, contours, hierarchy,
                    RetrType.External, ChainApproxMethod.ChainApproxSimple);

                // ── Extract + filter bounding rects ───────────────────────
                int minArea = (rows * cols) / 600;
                Log.Debug("CalParse.Table", $"Contours found: {contours.Size}  minArea={minArea}");
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

                Log.Debug("CalParse.Table", $"Cells after area filter: {rawCells.Count}");

                // Dispose native resources
                src.Dispose(); horizontal.Dispose(); vertical.Dispose();
                grid.Dispose(); inverted.Dispose(); closeKernel.Dispose();
                hKernel.Dispose(); vKernel.Dispose(); hierarchy.Dispose();

                var assigned = AssignRowCol(rawCells);
                if (assigned.Count > 0)
                {
                    int maxR = assigned.Max(c => c.Row);
                    int maxC = assigned.Max(c => c.Col);
                    Log.Debug("CalParse.Table", $"Grid assigned: {maxR + 1} rows x {maxC + 1} cols  ({assigned.Count} cells total)");
                    // Log first 40 cells
                    foreach (var cell in assigned.Take(40))
                        Log.Debug("CalParse.Table", $"  cell[{cell.Row},{cell.Col}] bounds=({cell.Bounds.X},{cell.Bounds.Y} {cell.Bounds.Width}x{cell.Bounds.Height})");
                }
                else
                {
                    Log.Debug("CalParse.Table", "Grid assigned: 0 cells — no table detected!");
                }
                return assigned;
            }, ct);
#else
            throw new PlatformNotSupportedException("Table detection is only supported on Android.");
#endif
        }

        /// <summary>
        /// Clusters cells into rows by Y-centroid proximity, then sorts each row by X.
        /// Assigns zero-based Row and Col indices.
        /// </summary>
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
