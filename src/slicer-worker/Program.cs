// Deprecated placeholder: The original combined slicer-worker has been superseded by
// dedicated engine workers (e.g., orcaslicer-worker, prusaslicer-worker).
// This project remains temporarily to avoid breaking references while migration completes.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "legacy-slicer-worker",
    status = "deprecated",
    message = "Use dedicated engine workers (orcaslicer-worker / prusaslicer-worker)."
}));

app.Run();
