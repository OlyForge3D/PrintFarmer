using System;

namespace Farm.Infrastructure.Contracts.Slicing;

public class RenewLeaseRequest
{
    public int LeaseDurationSeconds { get; set; } = 300;
}
