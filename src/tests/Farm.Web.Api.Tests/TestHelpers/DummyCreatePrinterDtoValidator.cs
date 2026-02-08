using Farm.Infrastructure.Discovery;
using FluentValidation;

namespace Farm.Web.Api.Tests.TestHelpers;

public class DummyCreatePrinterDtoValidator : AbstractValidator<CreatePrinterFromDiscoveryDto>
{
    public DummyCreatePrinterDtoValidator()
    {
        // no rules - always valid
    }
}
