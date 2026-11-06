using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class SliceJobPrinterIdentityTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatusAndHistory_PreserveSavedPrinterIdentity(bool pinned)
    {
        using HttpClient client = await _factory.CreateWorkerClientAsync(
            workerName: "Printer Identity Worker",
            username: "printer-identity-worker",
            email: "printer-identity-worker@example.com");
        using HttpResponseMessage submit = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = (await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>())!;
        Guid? printerId = pinned ? Guid.NewGuid() : null;

        // Historical results must retain their metadata even if the fleet printer was removed.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            SliceJob job = await db.SliceJobs.SingleAsync(value => value.Id == submitted.JobId);
            job.PrinterId = printerId;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage statusResponse = await client.GetAsync($"/api/slice/{submitted.JobId}");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, await statusResponse.Content.ReadAsStringAsync());
        SliceJobStatusResponse status = (await statusResponse.Content.ReadFromJsonAsync<SliceJobStatusResponse>())!;
        status.PrinterId.Should().Be(printerId);

        using HttpResponseMessage historyResponse = await client.GetAsync("/api/slice/my-jobs");
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK, await historyResponse.Content.ReadAsStringAsync());
        List<SliceJobStatusResponse> history = (await historyResponse.Content.ReadFromJsonAsync<List<SliceJobStatusResponse>>())!;
        history.Should().ContainSingle(job => job.Id == submitted.JobId)
            .Which.PrinterId.Should().Be(printerId);
    }
}
