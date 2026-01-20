namespace Farm.Web.Api.Infrastructure.Database;

public interface IMigrationStatusProvider
{
    MigrationStatus GetStatus();
}
