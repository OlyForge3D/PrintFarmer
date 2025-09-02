using System.Net.Http.Json;
using Farm.Web.Shared;

namespace Farm.Web.Client.Services;

public class JobQueueService
{
    private readonly HttpClient _httpClient;

    public JobQueueService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<JobQueuePrintJobDto>> GetJobsAsync()
    {
        try
        {
            var jobs = await _httpClient.GetFromJsonAsync<List<JobQueuePrintJobDto>>(new Uri("api/queue", UriKind.Relative));
            return jobs ?? new List<JobQueuePrintJobDto>();
        }
        catch (Exception)
        {
            return new List<JobQueuePrintJobDto>();
        }
    }

    public async Task<JobQueuePrintJobDto?> CreateJobAsync(JobQueuePrintJobDto job)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(new Uri("api/queue", UriKind.Relative), job);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<JobQueuePrintJobDto>();
            }
        }
        catch (Exception)
        {
            // Handle error
        }
        return null;
    }

    public async Task<bool> UpdateJobAsync(Guid id, JobQueuePrintJobDto job)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(new Uri($"api/queue/{id}", UriKind.Relative), job);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> DeleteJobAsync(Guid id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(new Uri($"api/queue/{id}", UriKind.Relative));
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> StartJobAsync(Guid id)
    {
        try
        {
            var response = await _httpClient.PostAsync(new Uri($"api/queue/{id}/start", UriKind.Relative), null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> CancelJobAsync(Guid id)
    {
        try
        {
            var response = await _httpClient.PostAsync(new Uri($"api/queue/{id}/cancel", UriKind.Relative), null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> ReorderJobsAsync(IReadOnlyList<Guid> jobIds)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(new Uri("api/queue/reorder", UriKind.Relative), jobIds);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
