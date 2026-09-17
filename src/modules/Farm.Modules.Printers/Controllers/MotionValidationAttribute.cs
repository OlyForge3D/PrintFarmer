using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Modules.Printers.Controllers;

/// <summary>Retains the motion API error code on MVC binding and required-field failures.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MotionValidationAttribute : ActionFilterAttribute
{
    public MotionValidationAttribute() => Order = -2001;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState)
            {
                Status = 400,
                Title = "Invalid motion request",
                Extensions = { ["code"] = "invalid" },
            });
        }
    }
}
