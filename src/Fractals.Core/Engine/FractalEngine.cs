using System.Diagnostics;
using Fractals.Core.Models;
using SkiaSharp;

namespace Fractals.Core.Engine;

public class FractalEngine : IFractalEngine
{
    private static readonly double Ln2 = Math.Log(2.0);

    public async Task<FractalResult> RenderAsync(
        FractalParameters parameters,
        Func<int, int, Task>? onProgressAsync = null,
        Func<Task<bool>>? checkCanceledAsync = null,
        int stripHeight = 64,
        int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int width = parameters.Width;
        int height = parameters.Height;
        int maxIter = parameters.MaxIterations;

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        IntPtr pixelsPtr = bitmap.GetPixels();

        double aspectRatio = (double)width / height;
        double rangeY = 2.0 / parameters.Zoom;
        double rangeX = rangeY * aspectRatio;

        double minX = parameters.CenterX - rangeX / 2.0;
        double maxY = parameters.CenterY + rangeY / 2.0;
        double stepX = rangeX / width;
        double stepY = rangeY / height;

        long actualCalculations = 0;
        int rowsDone = 0;
        int stripCount = (height + stripHeight - 1) / stripHeight;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount
        };

        for (int strip = 0; strip < stripCount; strip++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (checkCanceledAsync != null && await checkCanceledAsync())
            {
                throw new OperationCanceledException("Задача скасована через прапорець is_canceled.");
            }

            int startRow = strip * stripHeight;
            int endRow = Math.Min(startRow + stripHeight, height);

            Parallel.For(startRow, endRow, parallelOptions, y =>
            {
                double y0 = maxY - y * stepY;

                unsafe
                {
                    uint* rowPixels = (uint*)pixelsPtr + (long)y * width;

                    for (int x = 0; x < width; x++)
                    {
                        parallelOptions.CancellationToken.ThrowIfCancellationRequested();

                        double x0 = minX + x * stepX;
                        double continuous = CalculatePoint(
                            parameters.Type,
                            x0,
                            y0,
                            maxIter,
                            parameters.CRe,
                            parameters.CIm,
                            parameters.Smooth);

                        SKColor color = ColorPalette.GetColor(parameters.Palette, continuous, maxIter);
                        rowPixels[x] = (uint)color;
                    }
                }
            });

            rowsDone = endRow;
            Interlocked.Add(ref actualCalculations, (long)(endRow - startRow) * width);

            if (onProgressAsync != null)
            {
                await onProgressAsync(rowsDone, height);
            }
        }

        stopwatch.Stop();

        // Encode Full PNG
        using var image = SKImage.FromBitmap(bitmap);
        using var fullData = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] fullPng = fullData.ToArray();

        // Generate and Encode Thumbnail (max 200x200 maintaining aspect ratio)
        int thumbWidth = 200;
        int thumbHeight = (int)Math.Max(1, 200.0 * height / width);
        using var thumbBitmap = bitmap.Resize(new SKImageInfo(thumbWidth, thumbHeight), SKFilterQuality.Medium);
        using var thumbImage = SKImage.FromBitmap(thumbBitmap);
        using var thumbData = thumbImage.Encode(SKEncodedImageFormat.Png, 90);
        byte[] thumbPng = thumbData.ToArray();

        return new FractalResult(
            fullPng,
            thumbPng,
            stopwatch.Elapsed,
            actualCalculations);
    }

    public double CalculatePoint(
        FractalType type,
        double x0,
        double y0,
        int maxIter,
        double cRe,
        double cIm,
        bool smooth)
    {
        double zx, zy, cx, cy;

        if (type == FractalType.Mandelbrot)
        {
            zx = 0.0;
            zy = 0.0;
            cx = x0;
            cy = y0;

            // Cardioid and period-2 bulb optimization for Mandelbrot
            double q = (x0 - 0.25) * (x0 - 0.25) + y0 * y0;
            if (q * (q + (x0 - 0.25)) <= 0.25 * y0 * y0) return maxIter;
            if ((x0 + 1.0) * (x0 + 1.0) + y0 * y0 <= 0.0625) return maxIter;
        }
        else
        {
            zx = x0;
            zy = y0;
            cx = cRe;
            cy = cIm;
        }

        int iter = 0;
        double zx2 = zx * zx;
        double zy2 = zy * zy;

        while (zx2 + zy2 <= 4.0 && iter < maxIter)
        {
            zy = 2.0 * zx * zy + cy;
            zx = zx2 - zy2 + cx;
            zx2 = zx * zx;
            zy2 = zy * zy;
            iter++;
        }

        if (iter >= maxIter)
        {
            return maxIter;
        }

        if (!smooth)
        {
            return iter;
        }

        // Renormalization formula for smooth coloring
        double mod = Math.Sqrt(zx2 + zy2);
        if (mod <= 1.0) return iter;

        double logZn = Math.Log(mod);
        double nu = Math.Log(logZn / Ln2) / Ln2;
        return iter + 1.0 - nu;
    }
}
