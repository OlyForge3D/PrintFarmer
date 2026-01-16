using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Data.Repositories
{
    public interface IPrintApprovalRepository
    {
        Task AddAsync(PrintApproval approval);
        Task<PrintApproval?> GetAsync(Guid id);
        Task RemoveAsync(PrintApproval approval);
        Task<IEnumerable<PrintApproval>> ListPendingAsync();
    }

    public class EfPrintApprovalRepository : IPrintApprovalRepository
    {
        private readonly AppDbContext _db;

        public EfPrintApprovalRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

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
