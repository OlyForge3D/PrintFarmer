using Farm.Infrastructure;
using FluentValidation;

namespace Farm.Web.Api.Tests.TestHelpers;

public class DummyCreatePrinterDtoValidator : AbstractValidator<CreatePrinterDto>
{
    public DummyCreatePrinterDtoValidator()
    {
        // no rules - always valid
    }
}
