using FluentAssertions;
using Fractals.Core.Models;
using Fractals.Core.Validation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fractals.UnitTests;

public class LimitsValidationTests
{
    private readonly FractalParametersValidator _validator;

    public LimitsValidationTests()
    {
        var limits = new CalculationLimits
        {
            MaxCost = 1_000_000,
            MinWidth = 100,
            MaxWidth = 2000,
            MinHeight = 100,
            MaxHeight = 2000,
            MinIterations = 10,
            MaxIterations = 1000
        };

        _validator = new FractalParametersValidator(Options.Create(limits));
    }

    [Fact]
    public void ValidParameters_ShouldPassValidation()
    {
        var parameters = new FractalParameters
        {
            Width = 500,
            Height = 500,
            MaxIterations = 100, // Cost = 500 * 500 * 100 = 25,000,000 > 1,000,000, wait! Let's set 200 * 200 * 20 = 800,000 <= 1,000,000
            Zoom = 1.0
        };
        parameters = parameters with { Width = 200, Height = 200, MaxIterations = 20 };

        var result = _validator.Validate(parameters);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ExceedingMaxCost_ShouldFailWithUkrainianMessage()
    {
        var parameters = new FractalParameters
        {
            Width = 1000,
            Height = 1000,
            MaxIterations = 100 // Cost = 100,000,000 > 1,000,000
        };

        var result = _validator.Validate(parameters);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("перевищує встановлений ліміт"));
    }

    [Fact]
    public void DimensionsOutOfRange_ShouldFail()
    {
        var parameters = new FractalParameters
        {
            Width = 50, // Less than MinWidth (100)
            Height = 500,
            MaxIterations = 50
        };

        var result = _validator.Validate(parameters);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(FractalParameters.Width));
    }

    [Fact]
    public void InvalidZoom_ShouldFail()
    {
        var parameters = new FractalParameters
        {
            Width = 200,
            Height = 200,
            MaxIterations = 20,
            Zoom = -1.0
        };

        var result = _validator.Validate(parameters);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(FractalParameters.Zoom));
    }
}
