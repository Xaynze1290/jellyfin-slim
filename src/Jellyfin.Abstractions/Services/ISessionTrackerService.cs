using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Abstractions.Services
{
    public interface ISessionTrackerService
    {
        Task<CoreSessionInfo> CreateSessionAsync(CreateCoreSessionRequest request);
        Task<CoreSessionInfo?> GetSessionAsync(string sessionId); // Nullable if not found
        Task<IEnumerable<CoreSessionInfo>> GetUserSessionsAsync(string userId);
        Task EndSessionAsync(string sessionId);
        Task ReportSessionActivityAsync(string sessionId, bool isActive); // e.g., for keep-alive
        // Potentially methods for broadcasting messages, etc.
    }
}
