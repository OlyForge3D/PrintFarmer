namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Tag data transfer object for organizing and categorizing items.
/// </summary>
public class TagDto
{
    /// <summary>Gets or sets the tag identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tag name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the hex color for UI display.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the tag description.</summary>
    public string? Description { get; set; }
}
