using System;
using System.Collections.Generic;
using Jellyfin.Abstractions.Services;

namespace Jellyfin.CoreDefaults.Services
{
    public class ServiceOverrideHost : IServiceOverrideHost
    {
        private readonly Dictionary<Type, object> _overrides = new();

        public void RegisterOverride<TService>(TService implementation) where TService : class
        {
            var serviceType = GetServiceType<TService>();
            if (implementation == null)
            {
                throw new ArgumentNullException(nameof(implementation));
            }

            // Ensure the implementation is assignable to the service type,
            // especially if TService is an interface and implementation is a concrete type.
            // This check is mostly for safety, as generic constraints usually handle this.
            if (!serviceType.IsAssignableFrom(implementation.GetType()))
            {
                throw new ArgumentException($"Implementation type {implementation.GetType().FullName} is not assignable to service type {serviceType.FullName}", nameof(implementation));
            }

            _overrides[serviceType] = implementation;
        }

        public TService GetOverride<TService>() where TService : class
        {
            _overrides.TryGetValue(GetServiceType<TService>(), out var implementation);
            return (TService)implementation; // Returns null if not found or if type cast fails (shouldn't if registration is correct)
        }

        public bool HasOverride<TService>() where TService : class
        {
            return _overrides.ContainsKey(GetServiceType<TService>());
        }

        // Helper to consistently get the key type, especially if TService might be a concrete type used as key
        public Type GetServiceType<TService>() where TService : class
        {
            // If TService is an interface, typeof(TService) is correct.
            // If TService could be a concrete type being registered for itself (less common for overrides), still okay.
            return typeof(TService);
        }
    }
}
