using Farm.Web.Shared;
using FluentValidation;

namespace Farm.Web.Api.Tests.TestHelpers;

public class DummyCreatePrinterDtoValidator : AbstractValidator<CreatePrinterDto>
{
    public DummyCreatePrinterDtoValidator()
    {
        // no rules - always valid
    }
}
