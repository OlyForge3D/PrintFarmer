using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrintJobs;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Data.Repositories
{
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
