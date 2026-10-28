using FluentAssertions;
using Fractals.Core.Engine;
using Fractals.Core.Models;
using Xunit;

namespace Fractals.UnitTests;

public class FractalMathTests
{
    private readonly FractalEngine _engine = new();

    [Fact]
    public void Mandelbrot_Origin_ShouldNotEscape()
    {
        // Point (0,0) is well inside the Mandelbrot set
        double result = _engine.CalculatePoint(
            FractalType.Mandelbrot,
            x0: 0.0,
            y0: 0.0,
            maxIter: 100,
            cRe: 0,
            cIm: 0,
            smooth: false);

        result.Should().Be(100);
    }

    [Fact]
    public void Mandelbrot_PointFarAway_ShouldEscapeQuickly()
    {
        // Point (2, 2) is outside the radius of 2
        double result = _engine.CalculatePoint(
            FractalType.Mandelbrot,
            x0: 2.0,
            y0: 2.0,
            maxIter: 100,
            cRe: 0,
            cIm: 0,
            smooth: false);

        result.Should().BeLessThan(5);
    }

    [Fact]
    public void Mandelbrot_Period2Bulb_ShouldBeInSet()
    {
        // Point (-1, 0) is inside the period-2 bulb
        double result = _engine.CalculatePoint(
            FractalType.Mandelbrot,
            x0: -1.0,
            y0: 0.0,
            maxIter: 100,
            cRe: 0,
            cIm: 0,
            smooth: false);

        result.Should().Be(100);
    }

    [Fact]
    public void Julia_StandardPoint_ShouldComputeDeterministically()
    {
        double result1 = _engine.CalculatePoint(
            FractalType.Julia,
            x0: 0.1,
            y0: 0.2,
            maxIter: 150,
            cRe: -0.7,
            cIm: 0.27015,
            smooth: true);

        double result2 = _engine.CalculatePoint(
            FractalType.Julia,
            x0: 0.1,
            y0: 0.2,
            maxIter: 150,
            cRe: -0.7,
            cIm: 0.27015,
            smooth: true);

        result1.Should().Be(result2);
        result1.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RenderAsync_ProducesValidImages()
    {
        var parameters = new FractalParameters
        {
            Type = FractalType.Mandelbrot,
            Width = 128,
            Height = 128,
            MaxIterations = 50,
            Palette = PaletteType.Fire,
            Smooth = true
        };

        var result = await _engine.RenderAsync(parameters, stripHeight: 32);

        result.Should().NotBeNull();
        result.ImagePng.Should().NotBeEmpty();
        result.ThumbnailPng.Should().NotBeEmpty();
        result.ActualIterations.Should().BeGreaterThan(0);
    }
}

