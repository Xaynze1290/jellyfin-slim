using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Cryptography;
using Microsoft.Extensions.Logging;
using Jellyfin.Abstractions.Services; // Added for ICoreAuthenticationService
using System.Collections.Generic; // For Dictionary in DTOs

// Namespace and interface will be updated next.
namespace Jellyfin.CoreDefaults.Authentication // Changed namespace
{
    /// <summary>
    /// The default authentication provider.
    /// </summary>
    public class DefaultAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser, ICoreAuthenticationService // Added ICoreAuthenticationService
    {
        private readonly ILogger<DefaultAuthenticationProvider> _logger;
        private readonly ICryptoProvider _cryptographyProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAuthenticationProvider"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="cryptographyProvider">The cryptography provider.</param>
        public DefaultAuthenticationProvider(ILogger<DefaultAuthenticationProvider> logger, ICryptoProvider cryptographyProvider)
        {
            _logger = logger;
            _cryptographyProvider = cryptographyProvider;
        }

        /// <inheritdoc />
        public string Name => "Default";

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        // This is dumb and an artifact of the backwards way auth providers were designed.
        // This version of authenticate was never meant to be called, but needs to be here for interface compat
        // Only the providers that don't provide local user support use this
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        // This is the version that we need to use for local users. Because reasons.
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        {
            [DoesNotReturn]
            static void ThrowAuthenticationException()
            {
                throw new AuthenticationException("Invalid username or password");
            }

            if (resolvedUser is null)
            {
                ThrowAuthenticationException();
            }

            // As long as jellyfin supports password-less users, we need this little block here to accommodate
            if (!HasPassword(resolvedUser) && string.IsNullOrEmpty(password))
            {
                return Task.FromResult(new ProviderAuthenticationResult
                {
                    Username = username
                });
            }

            // Handle the case when the stored password is null, but the user tried to login with a password
            if (resolvedUser.Password is null)
            {
                ThrowAuthenticationException();
            }

            PasswordHash readyHash = PasswordHash.Parse(resolvedUser.Password);
            if (!_cryptographyProvider.Verify(readyHash, password))
            {
                ThrowAuthenticationException();
            }

            // Migrate old hashes to the new default
            if (!string.Equals(readyHash.Id, _cryptographyProvider.DefaultHashMethod, StringComparison.Ordinal)
                || int.Parse(readyHash.Parameters["iterations"], CultureInfo.InvariantCulture) != Constants.DefaultIterations)
            {
                _logger.LogInformation("Migrating password hash of {User} to the latest default", username);
                ChangePassword(resolvedUser, password);
            }

            return Task.FromResult(new ProviderAuthenticationResult
            {
                Username = username
            });
        }

        /// <inheritdoc />
        public bool HasPassword(User user)
            => !string.IsNullOrEmpty(user?.Password);

        /// <inheritdoc />
        public Task ChangePassword(User user, string newPassword)
        {
            if (string.IsNullOrEmpty(newPassword))
            {
                user.Password = null;
                return Task.CompletedTask;
            }

            PasswordHash newPasswordHash = _cryptographyProvider.CreatePasswordHash(newPassword);
            user.Password = newPasswordHash.ToString();

            return Task.CompletedTask;
        }

        // Stub implementation for ICoreAuthenticationService
        public async Task<CoreAuthResult> AuthenticateAsync(CoreAuthRequest request)
        {
            // This needs to adapt the existing Authenticate(username, password, resolvedUser) method.
            // It requires fetching the User object first. This is a simplified stub.
            // A proper implementation would involve IUserManager.GetUserByName or similar.
            _logger.LogDebug("ICoreAuthenticationService.AuthenticateAsync called for user {Username}", request.Username);

            // Placeholder: In a real scenario, you'd fetch the user via IUserManager.
            // For this stub, we can't directly call the old Authenticate without a User object.
            // Let's assume failure or a dummy success if no password.
            if (string.IsNullOrEmpty(request.Password))
            {
                 // This part of logic is similar to existing password-less user check
                 // but we don't have the resolvedUser here to check HasPassword(resolvedUser)
                 // Without user manager, cannot truly replicate.
                _logger.LogWarning("Attempting password-less login via ICoreAuthenticationService stub for user {Username}. This path needs IUserManager.", request.Username);
                // return new CoreAuthResult { IsSuccess = true, UserId = "placeholder-user-id-for-" + request.Username };
            }

            // This stub cannot fully replicate the original logic without access to IUserManager
            // to fetch the 'resolvedUser'. The original 'Authenticate' that takes 'resolvedUser'
            // is the one with the main logic.
            // Throwing NotImplementedException to indicate this needs proper wiring.
            throw new NotImplementedException("AuthenticateAsync in DefaultAuthenticationProvider requires IUserManager to fetch user details to call existing logic.");

            // Example of what it might look like if user was fetched:
            // User resolvedUser = await _userManager.GetUserByName(request.Username);
            // if (resolvedUser == null)
            // {
            //     return new CoreAuthResult { IsSuccess = false, ErrorMessage = "Invalid username or password" };
            // }
            // try
            // {
            //     ProviderAuthenticationResult providerResult = await Authenticate(request.Username, request.Password, resolvedUser);
            //     return new CoreAuthResult {
            //         IsSuccess = true,
            //         UserId = resolvedUser.Id.ToString(),
            //         // Populate UserProperties if needed
            //     };
            // }
            // catch (AuthenticationException ex)
            // {
            //     return new CoreAuthResult { IsSuccess = false, ErrorMessage = ex.Message };
            // }
        }
    }
}
