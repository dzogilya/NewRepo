using FluentValidation;
using Fractals.Core.Models;
using Microsoft.Extensions.Options;

namespace Fractals.Core.Validation;

public class FractalParametersValidator : AbstractValidator<FractalParameters>
{
    public FractalParametersValidator(IOptions<CalculationLimits> limitsOptions)
    {
        var limits = limitsOptions.Value;

        RuleFor(p => p.Width)
            .InclusiveBetween(limits.MinWidth, limits.MaxWidth)
            .WithMessage($"Ширина зображення повинна бути між {limits.MinWidth} та {limits.MaxWidth} пікселів.");

        RuleFor(p => p.Height)
            .InclusiveBetween(limits.MinHeight, limits.MaxHeight)
            .WithMessage($"Висота зображення повинна бути між {limits.MinHeight} та {limits.MaxHeight} пікселів.");

        RuleFor(p => p.MaxIterations)
            .InclusiveBetween(limits.MinIterations, limits.MaxIterations)
            .WithMessage($"Кількість ітерацій повинна бути між {limits.MinIterations} та {limits.MaxIterations}.");

        RuleFor(p => p.Zoom)
            .GreaterThan(0.0)
            .WithMessage("Масштаб (zoom) повинен бути більшим за 0.");

        RuleFor(p => p)
            .Must(p =>
            {
                try
                {
                    return p.CalculateCost() <= limits.MaxCost;
                }
                catch (OverflowException)
                {
                    return false;
                }
            })
            .WithMessage($"Загальна трудомісткість задачі перевищує встановлений ліміт ({limits.MaxCost:#,##0} операцій). Зменшіть розмір або кількість ітерацій.");
    }
}

