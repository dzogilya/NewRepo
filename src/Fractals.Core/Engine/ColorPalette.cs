using Fractals.Core.Models;
using SkiaSharp;

namespace Fractals.Core.Engine;

public static class ColorPalette
{
    public static SKColor GetColor(PaletteType type, double continuousIndex, int maxIterations)
    {
        if (continuousIndex >= maxIterations || double.IsNaN(continuousIndex) || continuousIndex < 0)
        {
            return SKColors.Black;
        }

        double t = (continuousIndex % 256.0) / 256.0;

        return type switch
        {
            PaletteType.Fire => GetFireColor(t),
            PaletteType.ElectricBlue => GetElectricBlueColor(t),
            PaletteType.Rainbow => GetRainbowColor(t),
            _ => GetClassicColor(t)
        };
    }

    private static SKColor GetClassicColor(double t)
    {
        byte r = (byte)(9 * (1 - t) * t * t * t * 255);
        byte g = (byte)(15 * (1 - t) * (1 - t) * t * t * 255);
        byte b = (byte)(8.5 * (1 - t) * (1 - t) * (1 - t) * t * 255);
        return new SKColor(r, g, b);
    }

    private static SKColor GetFireColor(double t)
    {
        byte r = (byte)Math.Clamp(t * 3.0 * 255, 0, 255);
        byte g = (byte)Math.Clamp((t * 3.0 - 1.0) * 255, 0, 255);
        byte b = (byte)Math.Clamp((t * 3.0 - 2.0) * 255, 0, 255);
        return new SKColor(r, g, b);
    }

    private static SKColor GetElectricBlueColor(double t)
    {
        byte r = (byte)Math.Clamp((Math.Sin(t * Math.PI * 2) * 0.5 + 0.5) * 50, 0, 255);
        byte g = (byte)Math.Clamp((Math.Sin(t * Math.PI * 2 + 1) * 0.5 + 0.5) * 180 + 75, 0, 255);
        byte b = (byte)Math.Clamp((Math.Cos(t * Math.PI * 2) * 0.5 + 0.5) * 200 + 55, 0, 255);
        return new SKColor(r, g, b);
    }

    private static SKColor GetRainbowColor(double t)
    {
        double h = t * 360.0;
        return SKColor.FromHsv((float)h, 100, 100);
    }
}
