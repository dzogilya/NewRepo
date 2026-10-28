using Fractals.Core.Models;

namespace Fractals.Core.Engine;

public interface IFractalEngine
{
    Task<FractalResult> RenderAsync(
        FractalParameters parameters,
        Func<int, int, Task>? onProgressAsync = null,
        Func<Task<bool>>? checkCanceledAsync = null,
        int stripHeight = 64,
        int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default);

    double CalculatePoint(FractalType type, double x0, double y0, int maxIter, double cRe, double cIm, bool smooth);
}

public record FractalResult(
    byte[] ImagePng,
    byte[] ThumbnailPng,
    TimeSpan Elapsed,
    long ActualIterations);

