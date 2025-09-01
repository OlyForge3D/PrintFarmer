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
            var jobs = await _httpClient.GetFromJsonAsync<List<JobQueuePrintJobDto>>("api/queue");
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
            var response = await _httpClient.PostAsJsonAsync("api/queue", job);
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
            var response = await _httpClient.PutAsJsonAsync($"api/queue/{id}", job);
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
            var response = await _httpClient.DeleteAsync($"api/queue/{id}");
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
            var response = await _httpClient.PostAsync($"api/queue/{id}/start", null);
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
            var response = await _httpClient.PostAsync($"api/queue/{id}/cancel", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> ReorderJobsAsync(List<Guid> jobIds)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/queue/reorder", jobIds);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
