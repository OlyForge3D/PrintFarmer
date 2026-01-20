using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

// Legacy monolithic SlicerController removed after refactor into specialized controllers.
// Kept as empty placeholder (ignored by API explorer) to avoid breaking any lingering references.
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/legacy-slicer-placeholder")]
public class SlicerController : ControllerBase
{
}
