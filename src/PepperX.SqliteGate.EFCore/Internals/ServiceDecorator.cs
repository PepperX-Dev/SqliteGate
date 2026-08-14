using Microsoft.Extensions.DependencyInjection;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Wraps a service EF Core has already registered, keeping whatever implementation the provider
/// chose and putting ours in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReplaceService&lt;TService, TImplementation&gt;()</c> is the obvious tool and the wrong one
/// here: it substitutes a registration rather than decorating it, and offers no way to reach the
/// implementation being replaced. Constructing that implementation ourselves means naming it, and
/// every one of these lives in EF Core's <c>Internal</c> namespace with no compatibility promise.
/// </para>
/// <para>
/// Reading the existing <see cref="ServiceDescriptor"/> and building the original through
/// <see cref="ActivatorUtilities"/> avoids naming anything: whatever the SQLite provider registered
/// is what gets constructed, including through any future change to its constructor. The decorator
/// only has to know the public interface it is wrapping.
/// </para>
/// </remarks>
internal static class ServiceDecorator
{
    /// <summary>
    /// Replaces the registration for <typeparamref name="TService"/> with one that builds the
    /// original and hands it to <paramref name="decorate"/>.
    /// </summary>
    /// <typeparam name="TService">The service interface to wrap.</typeparam>
    /// <param name="services">EF Core's internal service collection.</param>
    /// <param name="decorate">Receives the service provider and the original implementation.</param>
    /// <returns><see langword="true"/> when a registration was found and wrapped.</returns>
    internal static bool Decorate<TService>(
        IServiceCollection services,
        Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];

            if (descriptor.ServiceType != typeof(TService) || descriptor.IsKeyedService)
            {
                continue;
            }

            services[index] = ServiceDescriptor.Describe(
                typeof(TService),
                provider => decorate(provider, (TService)CreateOriginal(provider, descriptor)),
                descriptor.Lifetime);

            return true;
        }

        return false;
    }

    private static object CreateOriginal(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        return ActivatorUtilities.CreateInstance(
            provider,
            descriptor.ImplementationType
                ?? throw new SqliteGateException(
                    $"The registration for '{descriptor.ServiceType}' has no implementation to decorate."));
    }
}
