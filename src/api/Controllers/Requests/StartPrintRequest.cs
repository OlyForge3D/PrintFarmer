namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;

public sealed record StartPrintRequest([Required, MinLength(1)] string Filename);
