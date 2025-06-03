using System.Threading.Tasks;

namespace Jellyfin.Abstractions.Services
{
    public interface ICoreAuthenticationService
    {
        Task<CoreAuthResult> AuthenticateAsync(CoreAuthRequest request);
        // Potentially methods for registration, password change, etc.
        // Task<bool> ChangePasswordAsync(string userId, string oldPassword, string newPassword);
    }
}
