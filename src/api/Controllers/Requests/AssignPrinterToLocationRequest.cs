using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request DTO for assigning a printer to a location.
/// </summary>
public class AssignPrinterToLocationRequest
{
    /// <summary>
    /// The ID of the location to assign the printer to.
    /// </summary>
    [Required(ErrorMessage = "LocationId is required")]
    public Guid? LocationId { get; set; }
}
