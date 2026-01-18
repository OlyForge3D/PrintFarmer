namespace Farm.Backend.Plugin.Core;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

/// <summary>
/// Extension methods for registering HTTP clients from plugin code.
/// Provides reflection-based AddHttpClient support since plugins cannot directly reference API types.
/// </summary>
public static class HttpClientRegistrationExtensions
{
    /// <summary>
    /// Registers an HTTP client using reflection to support dynamic type loading in plugins.
    /// Equivalent to services.AddHttpClient&lt;TInterface, TImplementation&gt;(configureClient).
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="interfaceType">The interface type for the HTTP client.</param>
    /// <param name="implementationType">The implementation type for the HTTP client.</param>
    /// <param name="configureClient">Action to configure the HTTP client (e.g., timeout).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpClientFromPlugin(
        this IServiceCollection services,
        Type interfaceType,
        Type implementationType,
        Action<System.Net.Http.HttpClient>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(implementationType);

        try
        {
            // Get the AddHttpClient generic method that accepts an Action<HttpClient>
            // We need the overload: AddHttpClient<TInterface, TImplementation>(IServiceCollection, Action<HttpClient>)
            var addHttpClientMethod = typeof(HttpClientFactoryServiceCollectionExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "AddHttpClient" || !m.IsGenericMethodDefinition)
                    {
                        return false;
                    }

                    var genericArgs = m.GetGenericArguments();
                    if (genericArgs.Length != 2)
                    {
                        return false;
                    }

                    var parameters = m.GetParameters();
                    // We want the overload with (IServiceCollection, Action<HttpClient>) parameters
                    if (parameters.Length != 2)
                    {
                        return false;
                    }

                    // Check if first param is IServiceCollection
                    if (parameters[0].ParameterType != typeof(IServiceCollection))
                    {
                        return false;
                    }

                    // Check if second param is Action<HttpClient>
                    var secondParamType = parameters[1].ParameterType;
                    var expectedActionType = typeof(Action<System.Net.Http.HttpClient>);
                    return secondParamType == expectedActionType;
                });

            if (addHttpClientMethod == null)
            {
                throw new InvalidOperationException("Could not find AddHttpClient<TInterface, TImplementation>(IServiceCollection, Action<HttpClient>) method");
            }

            // Make the generic method with our types
            var genericMethod = addHttpClientMethod.MakeGenericMethod(interfaceType, implementationType);

            // Invoke it with the service collection and configuration action
            genericMethod.Invoke(null, new object?[] { services, configureClient });

            return services;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error registering HTTP client for {interfaceType.Name}: {ex.InnerException?.Message ?? ex.Message}");
            throw;
        }
    }
}
