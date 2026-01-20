namespace Farm.Web.Api.Services.OctoPrint
{
    public class OctoPrintSettings
    {
        public bool RequireApiKey { get; set; } = false;

        public int RateLimitPerMinute { get; set; } = 60;

        public int MaxUploadSizeMb { get; set; } = 50;
    }
}
