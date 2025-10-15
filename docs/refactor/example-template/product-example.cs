// Example: Product scaffold converted to docs example template

// IProductService
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Docs.Refactor.Example
{
    public interface IProductService
    {
        Task<IEnumerable<ProductDto>> GetAllAsync();
        Task<ProductDto?> GetByIdAsync(Guid id);
        Task<ProductDto> CreateAsync(CreateProductRequest request);
    }

    // IProductRepository
    public interface IProductRepository
    {
        Task<IEnumerable<Product>> GetAllAsync();
        Task<Product?> GetByIdAsync(Guid id);
        Task<Product> AddAsync(Product entity);
    }

    // DTOs
    public record ProductDto(Guid Id, string Name, string? Description);

    public class CreateProductRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
