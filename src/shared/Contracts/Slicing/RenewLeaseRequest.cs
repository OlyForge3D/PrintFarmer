using System;

namespace Farm.Web.Shared.Contracts.Slicing
{
    public class RenewLeaseRequest
    {
        public int LeaseDurationSeconds { get; set; } = 300;
    }
}
