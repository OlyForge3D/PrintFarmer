using System.Threading.Tasks;

namespace Farm.Web.Api.Services.Interfaces
{
    public interface IDatabaseInitializer
    {
        Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5);

        Task SeedAllAsync();
    }
}
