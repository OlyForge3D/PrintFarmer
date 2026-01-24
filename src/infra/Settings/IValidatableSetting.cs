using System.Threading.Tasks;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Interface for settings classes that support validation.
/// Implementations should throw an exception if validation fails.
/// </summary>
public interface IValidatableSetting
{
    /// <summary>
    /// Validates the settings values.
    /// </summary>
    /// <exception cref="System.ArgumentException">Thrown when validation fails.</exception>
    void Validate();

    // Optionally: Task ValidateAsync();
}
