using FluentValidation;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests.TestHelpers;

public class DummyCreatePrinterDtoValidator : FluentValidation.AbstractValidator<Farm.Web.Shared.CreatePrinterDto>
{
    public DummyCreatePrinterDtoValidator()
    {
        // no rules - always valid
    }
}
