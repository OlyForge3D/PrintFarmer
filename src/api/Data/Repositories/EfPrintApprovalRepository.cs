using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Data.Repositories
{
    /// <summary>
    /// Repository for managing print approval records requiring user confirmation before printing.
    /// </summary>
    public interface IPrintApprovalRepository
    {
        /// <summary>
        /// Adds a new print approval request.
        /// </summary>
        /// <param name="approval">The approval to add.</param>
        Task AddAsync(PrintApproval approval);

        /// <summary>
        /// Gets a print approval by its ID.
        /// </summary>
        /// <param name="id">The approval ID.</param>
        /// <returns>The approval if found; otherwise null.</returns>
        Task<PrintApproval?> GetAsync(Guid id);

        /// <summary>
        /// Removes a print approval record.
        /// </summary>
        /// <param name="approval">The approval to remove.</param>
        Task RemoveAsync(PrintApproval approval);

        /// <summary>
        /// Lists all pending print approvals awaiting user action.
        /// </summary>
        /// <returns>Collection of pending approvals.</returns>
        Task<IEnumerable<PrintApproval>> ListPendingAsync();
    }

    public class EfPrintApprovalRepository(AppDbContext db) : IPrintApprovalRepository
    {
        private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

        public async Task AddAsync(PrintApproval approval)
        {
            await _db.Set<PrintApproval>().AddAsync(approval);
            await _db.SaveChangesAsync();
        }

        public Task<PrintApproval?> GetAsync(Guid id)
        {
            return _db.Set<PrintApproval>().FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task RemoveAsync(PrintApproval approval)
        {
            _db.Set<PrintApproval>().Remove(approval);
            await _db.SaveChangesAsync();
        }

        public Task<IEnumerable<PrintApproval>> ListPendingAsync()
        {
            return Task.FromResult((IEnumerable<PrintApproval>)_db.Set<PrintApproval>().AsNoTracking().ToArray());
        }
    }
}
