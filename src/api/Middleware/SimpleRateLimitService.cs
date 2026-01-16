using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Middleware
{
    public class SimpleRateLimitService
    {
        private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _counts = new();

        public bool TryConsume(string key, int limit, TimeSpan window)
        {
            var now = DateTime.UtcNow;
            var entry = _counts.GetOrAdd(key, _ => (0, now));
            if ((now - entry.WindowStart) > window)
            {
                _counts[key] = (1, now);
                return true;
            }

            if (entry.Count + 1 > limit)
            {
                return false;
            }

            _counts[key] = (entry.Count + 1, entry.WindowStart);
            return true;
        }
    }
}
