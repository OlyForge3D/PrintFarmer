using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Slicing
{
    public interface IProfileDuplicateFilter
    {
        Task<IReadOnlyList<ProcessProfile>> FilterAsync(IEnumerable<ProcessProfile> candidates, CancellationToken ct = default);
    }

    /// <summary>
    /// Centralized helper for removing duplicate slicer profiles prior to persistence.
    /// Handles both unique index constraints:
    ///  - (Name, SlicerType, PrinterModelId)
    ///  - Hash
    /// Also eliminates duplicates within the candidate set itself.
    /// </summary>
    public class ProfileDuplicateFilter : IProfileDuplicateFilter
    {
        private readonly AppDbContext _db;
        private readonly ILogger<ProfileDuplicateFilter> _logger;

        public ProfileDuplicateFilter(AppDbContext db, ILogger<ProfileDuplicateFilter> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<ProcessProfile>> FilterAsync(IEnumerable<ProcessProfile> candidates, CancellationToken ct = default)
        {
            List<ProcessProfile> list = candidates.Where(p => p != null).ToList();
            if (list.Count == 0)
            {
                return Array.Empty<ProcessProfile>();
            }

            // Preload existing uniqueness keys
            var existingKeyTuples = await _db.ProcessProfiles
                .Select(p => new { p.Name, p.SlicerType, p.PrinterModelId })
                .ToListAsync(ct);
            var existingHashSet = (await _db.ProcessProfiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Hash))
                .Select(p => p.Hash!)
                .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingKeySet = existingKeyTuples
                .Select(k => (k.Name, k.SlicerType, k.PrinterModelId))
                .ToHashSet();

            // Track duplicates among candidates themselves
            HashSet<(string? Name, SlicerType SlicerType, Guid? PrinterModelId)> candidateKeys = new();
            HashSet<string> candidateHashes = new(StringComparer.OrdinalIgnoreCase);

            List<ProcessProfile> filtered = new();
            int skipped = 0;

            foreach (var profile in list)
            {
                string name = profile.Name ?? string.Empty;
                var composite = (name, profile.SlicerType, profile.PrinterModelId);

                // If hash missing but RawJson present, compute deterministic SHA256
                if (string.IsNullOrWhiteSpace(profile.Hash) && !string.IsNullOrWhiteSpace(profile.RawJson))
                {
                    profile.Hash = ComputeSha256(profile.RawJson);
                }
                string? hash = profile.Hash;

                bool duplicate = existingKeySet.Contains(composite)
                                  || candidateKeys.Contains(composite)
                                  || (!string.IsNullOrWhiteSpace(hash) && (existingHashSet.Contains(hash) || candidateHashes.Contains(hash)));

                if (duplicate)
                {
                    skipped++;
                    continue;
                }

                _ = candidateKeys.Add(composite);
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    _ = candidateHashes.Add(hash);
                }
                filtered.Add(profile);
            }

            if (skipped > 0)
            {
                _logger.LogInformation("[ProfileDuplicateFilter] Skipped {Skipped} duplicate profile(s) out of {Total} candidates.", skipped, list.Count);
            }
            else
            {
                _logger.LogDebug("[ProfileDuplicateFilter] No duplicates detected among {Total} candidates.", list.Count);
            }

            return filtered;
        }

        private static string ComputeSha256(string input)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
