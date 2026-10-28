namespace Fractals.Core.Models;

public record FractalParameters
{
    public FractalType Type { get; init; } = FractalType.Mandelbrot;
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 600;
    public int MaxIterations { get; init; } = 250;
    public double CenterX { get; init; } = -0.7;
    public double CenterY { get; init; } = 0.0;
    public double Zoom { get; init; } = 1.0;
    public double CRe { get; init; } = -0.7;
    public double CIm { get; init; } = 0.27015;
    public PaletteType Palette { get; init; } = PaletteType.Classic;
    public bool Smooth { get; init; } = true;

    public long CalculateCost()
    {
        checked
        {
            return (long)Width * Height * MaxIterations;
        }
    }
}
