namespace Farm.Infrastructure.Contracts.SignalR;

public class SignalRTestRequest
{
    public string? ConnectionId { get; set; }
    public string? GroupName { get; set; }
    public string? Message { get; set; }
}

public class DiscoveryTestRequest
{
    public string? SessionId { get; set; }
    public bool DelayBetweenMessages { get; set; } = true;
}
