using System.Threading.Tasks;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Interface for settings classes that support validation.
    /// </summary>
    public interface IValidatableSetting
    {
        void Validate();
        // Optionally: Task ValidateAsync();
    }
}
