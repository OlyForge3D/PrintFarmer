using System.Net.Http.Headers;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>Small helpers shared across emulator contract tests.</summary>
public static class TestRequests
{
    public static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// Uploads a minimal gcode file into the "gcodes" root through the real
    /// <c>server/files/upload</c> route, so tests that need a print-startable filename (print/start
    /// now requires the file to actually exist — see the "print-start fidelity" fix) can seed one
    /// without reaching into emulator internals.
    /// </summary>
    public static async Task EnsureGcodeFileExistsAsync(HttpClient client, string filename)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("; seeded for test\nG28\n"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", filename);
        form.Add(new StringContent("gcodes"), "root");
        using HttpResponseMessage response = await client.PostAsync("/server/files/upload", form);
        response.EnsureSuccessStatusCode();
    }
}
