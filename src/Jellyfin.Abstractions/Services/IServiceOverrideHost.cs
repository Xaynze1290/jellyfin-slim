using System;

namespace Jellyfin.Abstractions.Services
{
    public interface IServiceOverrideHost
    {
        void RegisterOverride<TService>(TService implementation) where TService : class;
        TService GetOverride<TService>() where TService : class;
        bool HasOverride<TService>() where TService : class;
        Type GetServiceType<TService>() where TService : class;
    }
}
