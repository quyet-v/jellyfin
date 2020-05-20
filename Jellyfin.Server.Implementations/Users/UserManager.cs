﻿#pragma warning disable CA1307

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Common.Cryptography;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Manages the creation and retrieval of <see cref="User"/> instances.
    /// </summary>
    public class UserManager : IUserManager
    {
        private readonly JellyfinDbProvider _dbProvider;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly INetworkManager _networkManager;
        private readonly IApplicationHost _appHost;
        private readonly IImageProcessor _imageProcessor;
        private readonly ILogger<IUserManager> _logger;

        private IAuthenticationProvider[] _authenticationProviders;
        private DefaultAuthenticationProvider _defaultAuthenticationProvider;
        private InvalidAuthProvider _invalidAuthProvider;
        private IPasswordResetProvider[] _passwordResetProviders;
        private DefaultPasswordResetProvider _defaultPasswordResetProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        /// <param name="cryptoProvider">The cryptography provider.</param>
        /// <param name="networkManager">The network manager.</param>
        /// <param name="appHost">The application host.</param>
        /// <param name="imageProcessor">The image processor.</param>
        /// <param name="logger">The logger.</param>
        public UserManager(
            JellyfinDbProvider dbProvider,
            ICryptoProvider cryptoProvider,
            INetworkManager networkManager,
            IApplicationHost appHost,
            IImageProcessor imageProcessor,
            ILogger<IUserManager> logger)
        {
            _dbProvider = dbProvider;
            _cryptoProvider = cryptoProvider;
            _networkManager = networkManager;
            _appHost = appHost;
            _imageProcessor = imageProcessor;
            _logger = logger;
        }

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>> OnUserPasswordChanged;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>> OnUserUpdated;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>> OnUserCreated;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>> OnUserDeleted;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>> OnUserLockedOut;

        /// <inheritdoc/>
        public IEnumerable<User> Users
        {
            get
            {
                var dbContext = _dbProvider.CreateContext();
                return dbContext.Users;
            }
        }

        /// <inheritdoc/>
        public IEnumerable<Guid> UsersIds
        {
            get
            {
                var dbContext = _dbProvider.CreateContext();
                return dbContext.Users.Select(u => u.Id);
            }
        }

        /// <inheritdoc/>
        public User GetUserById(Guid id)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Guid can't be empty", nameof(id));
            }

            var dbContext = _dbProvider.CreateContext();

            return dbContext.Users.Find(id);
        }

        /// <inheritdoc/>
        public User GetUserByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Invalid username", nameof(name));
            }

            var dbContext = _dbProvider.CreateContext();

            // This can't use an overload with StringComparer because that would cause the query to
            // have to be evaluated client-side.
            return dbContext.Users.FirstOrDefault(u => string.Equals(u.Username, name));
        }

        /// <inheritdoc/>
        public async Task RenameUser(User user, string newName)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("Invalid username", nameof(newName));
            }

            if (user.Username.Equals(newName, StringComparison.Ordinal))
            {
                throw new ArgumentException("The new and old names must be different.");
            }

            if (Users.Any(u => u.Id != user.Id && u.Username.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "A user with the name '{0}' already exists.",
                    newName));
            }

            user.Username = newName;
            await UpdateUserAsync(user).ConfigureAwait(false);

            OnUserUpdated?.Invoke(this, new GenericEventArgs<User>(user));
        }

        /// <inheritdoc/>
        public void UpdateUser(User user)
        {
            var dbContext = _dbProvider.CreateContext();
            dbContext.Users.Update(user);
            dbContext.SaveChanges();
        }

        /// <inheritdoc/>
        public async Task UpdateUserAsync(User user)
        {
            var dbContext = _dbProvider.CreateContext();
            dbContext.Users.Update(user);

            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public User CreateUser(string name)
        {
            if (!IsValidUsername(name))
            {
                throw new ArgumentException("Usernames can contain unicode symbols, numbers (0-9), dashes (-), underscores (_), apostrophes ('), and periods (.)");
            }

            var dbContext = _dbProvider.CreateContext();

            var newUser = new User(
                name,
                _defaultAuthenticationProvider.GetType().FullName,
                _defaultPasswordResetProvider.GetType().FullName);
            dbContext.Users.Add(newUser);
            dbContext.SaveChanges();

            OnUserCreated?.Invoke(this, new GenericEventArgs<User>(newUser));

            return newUser;
        }

        /// <inheritdoc/>
        public void DeleteUser(User user)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            var dbContext = _dbProvider.CreateContext();

            if (!dbContext.Users.Contains(user))
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The user cannot be deleted because there is no user with the Name {0} and Id {1}.",
                    user.Username,
                    user.Id));
            }

            if (dbContext.Users.Count() == 1)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The user '{0}' cannot be deleted because there must be at least one user in the system.",
                    user.Username));
            }

            if (user.HasPermission(PermissionKind.IsAdministrator)
                && Users.Count(i => i.HasPermission(PermissionKind.IsAdministrator)) == 1)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The user '{0}' cannot be deleted because there must be at least one admin user in the system.",
                        user.Username),
                    nameof(user));
            }

            dbContext.Users.Remove(user);
            dbContext.SaveChanges();
            OnUserDeleted?.Invoke(this, new GenericEventArgs<User>(user));
        }

        /// <inheritdoc/>
        public Task ResetPassword(User user)
        {
            return ChangePassword(user, string.Empty);
        }

        /// <inheritdoc/>
        public void ResetEasyPassword(User user)
        {
            ChangeEasyPassword(user, string.Empty, null);
        }

        /// <inheritdoc/>
        public async Task ChangePassword(User user, string newPassword)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            await GetAuthenticationProvider(user).ChangePassword(user, newPassword).ConfigureAwait(false);
            await UpdateUserAsync(user).ConfigureAwait(false);

            OnUserPasswordChanged?.Invoke(this, new GenericEventArgs<User>(user));
        }

        /// <inheritdoc/>
        public void ChangeEasyPassword(User user, string newPassword, string newPasswordSha1)
        {
            GetAuthenticationProvider(user).ChangeEasyPassword(user, newPassword, newPasswordSha1);
            UpdateUser(user);

            OnUserPasswordChanged?.Invoke(this, new GenericEventArgs<User>(user));
        }

        /// <inheritdoc/>
        public UserDto GetUserDto(User user, string remoteEndPoint = null)
        {
            return new UserDto
            {
                Name = user.Username,
                Id = user.Id,
                ServerId = _appHost.SystemId,
                HasPassword = user.Password == null,
                EnableAutoLogin = user.EnableAutoLogin,
                LastLoginDate = user.LastLoginDate,
                LastActivityDate = user.LastActivityDate,
                PrimaryImageTag = user.ProfileImage != null ? _imageProcessor.GetImageCacheTag(user) : null,
                Configuration = new UserConfiguration
                {
                    SubtitleMode = user.SubtitleMode,
                    HidePlayedInLatest = user.HidePlayedInLatest,
                    EnableLocalPassword = user.EnableLocalPassword,
                    PlayDefaultAudioTrack = user.PlayDefaultAudioTrack,
                    DisplayCollectionsView = user.DisplayCollectionsView,
                    DisplayMissingEpisodes = user.DisplayMissingEpisodes,
                    AudioLanguagePreference = user.AudioLanguagePreference,
                    RememberAudioSelections = user.RememberAudioSelections,
                    EnableNextEpisodeAutoPlay = user.EnableNextEpisodeAutoPlay,
                    RememberSubtitleSelections = user.RememberSubtitleSelections,
                    SubtitleLanguagePreference = user.SubtitleLanguagePreference ?? string.Empty,
                    OrderedViews = user.GetPreference(PreferenceKind.OrderedViews),
                    GroupedFolders = user.GetPreference(PreferenceKind.GroupedFolders),
                    MyMediaExcludes = user.GetPreference(PreferenceKind.MyMediaExcludes),
                    LatestItemsExcludes = user.GetPreference(PreferenceKind.LatestItemExcludes)
                },
                Policy = new UserPolicy
                {
                    MaxParentalRating = user.MaxParentalAgeRating,
                    EnableUserPreferenceAccess = user.EnableUserPreferenceAccess,
                    RemoteClientBitrateLimit = user.RemoteClientBitrateLimit.GetValueOrDefault(),
                    AuthenticationProviderId = user.AuthenticationProviderId,
                    PasswordResetProviderId = user.PasswordResetProviderId,
                    InvalidLoginAttemptCount = user.InvalidLoginAttemptCount,
                    LoginAttemptsBeforeLockout = user.LoginAttemptsBeforeLockout.GetValueOrDefault(),
                    IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
                    IsHidden = user.HasPermission(PermissionKind.IsHidden),
                    IsDisabled = user.HasPermission(PermissionKind.IsDisabled),
                    EnableSharedDeviceControl = user.HasPermission(PermissionKind.EnableSharedDeviceControl),
                    EnableRemoteAccess = user.HasPermission(PermissionKind.EnableRemoteAccess),
                    EnableLiveTvManagement = user.HasPermission(PermissionKind.EnableLiveTvManagement),
                    EnableLiveTvAccess = user.HasPermission(PermissionKind.EnableLiveTvAccess),
                    EnableMediaPlayback = user.HasPermission(PermissionKind.EnableMediaPlayback),
                    EnableAudioPlaybackTranscoding = user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding),
                    EnableVideoPlaybackTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
                    EnableContentDeletion = user.HasPermission(PermissionKind.EnableContentDeletion),
                    EnableContentDownloading = user.HasPermission(PermissionKind.EnableContentDownloading),
                    EnableSyncTranscoding = user.HasPermission(PermissionKind.EnableSyncTranscoding),
                    EnableMediaConversion = user.HasPermission(PermissionKind.EnableMediaConversion),
                    EnableAllChannels = user.HasPermission(PermissionKind.EnableAllChannels),
                    EnableAllDevices = user.HasPermission(PermissionKind.EnableAllDevices),
                    EnableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders),
                    EnableRemoteControlOfOtherUsers = user.HasPermission(PermissionKind.EnableRemoteControlOfOtherUsers),
                    EnablePlaybackRemuxing = user.HasPermission(PermissionKind.EnablePlaybackRemuxing),
                    ForceRemoteSourceTranscoding = user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding),
                    EnablePublicSharing = user.HasPermission(PermissionKind.EnablePublicSharing),
                    AccessSchedules = user.AccessSchedules.ToArray(),
                    BlockedTags = user.GetPreference(PreferenceKind.BlockedTags),
                    EnabledChannels = user.GetPreference(PreferenceKind.EnabledChannels),
                    EnabledDevices = user.GetPreference(PreferenceKind.EnabledDevices),
                    EnabledFolders = user.GetPreference(PreferenceKind.EnabledFolders),
                    EnableContentDeletionFromFolders = user.GetPreference(PreferenceKind.EnableContentDeletionFromFolders)
                }
            };
        }

        /// <inheritdoc/>
        public PublicUserDto GetPublicUserDto(User user, string remoteEndPoint = null)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            bool hasConfiguredPassword = GetAuthenticationProvider(user).HasPassword(user);
            bool hasConfiguredEasyPassword = !string.IsNullOrEmpty(GetAuthenticationProvider(user).GetEasyPasswordHash(user));

            bool hasPassword = user.EnableLocalPassword &&
                               !string.IsNullOrEmpty(remoteEndPoint) &&
                               _networkManager.IsInLocalNetwork(remoteEndPoint) ? hasConfiguredEasyPassword : hasConfiguredPassword;

            return new PublicUserDto
            {
                Name = user.Username,
                HasPassword = hasPassword,
                HasConfiguredPassword = hasConfiguredPassword
            };
        }

        /// <inheritdoc/>
        public async Task<User> AuthenticateUser(
            string username,
            string password,
            string passwordSha1,
            string remoteEndPoint,
            bool isUserSession)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogInformation("Authentication request without username has been denied (IP: {IP}).", remoteEndPoint);
                throw new ArgumentNullException(nameof(username));
            }

            var user = Users.ToList().FirstOrDefault(i => string.Equals(username, i.Username, StringComparison.OrdinalIgnoreCase));
            bool success;
            IAuthenticationProvider authenticationProvider;

            if (user != null)
            {
                var authResult = await AuthenticateLocalUser(username, password, user, remoteEndPoint)
                    .ConfigureAwait(false);
                authenticationProvider = authResult.authenticationProvider;
                success = authResult.success;
            }
            else
            {
                var authResult = await AuthenticateLocalUser(username, password, null, remoteEndPoint)
                    .ConfigureAwait(false);
                authenticationProvider = authResult.authenticationProvider;
                string updatedUsername = authResult.username;
                success = authResult.success;

                if (success
                    && authenticationProvider != null
                    && !(authenticationProvider is DefaultAuthenticationProvider))
                {
                    // Trust the username returned by the authentication provider
                    username = updatedUsername;

                    // Search the database for the user again
                    // the authentication provider might have created it
                    user = Users
                        .ToList().FirstOrDefault(i => string.Equals(username, i.Username, StringComparison.OrdinalIgnoreCase));

                    if (authenticationProvider is IHasNewUserPolicy hasNewUserPolicy)
                    {
                        UpdatePolicy(user.Id, hasNewUserPolicy.GetNewUserPolicy());

                        await UpdateUserAsync(user).ConfigureAwait(false);
                    }
                }
            }

            if (success && user != null && authenticationProvider != null)
            {
                var providerId = authenticationProvider.GetType().FullName;

                if (!string.Equals(providerId, user.AuthenticationProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    user.AuthenticationProviderId = providerId;
                    await UpdateUserAsync(user).ConfigureAwait(false);
                }
            }

            if (user == null)
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new AuthenticationException("Invalid username or password entered.");
            }

            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied because this account is currently disabled (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException(
                    $"The {user.Username} account is currently disabled. Please consult with your administrator.");
            }

            if (!user.HasPermission(PermissionKind.EnableRemoteAccess) &&
                !_networkManager.IsInLocalNetwork(remoteEndPoint))
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} forbidden: remote access disabled and user not in local network (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException("Forbidden.");
            }

            if (!user.IsParentalScheduleAllowed())
            {
                _logger.LogInformation(
                    "Authentication request for {UserName} is not allowed at this time due parental restrictions (IP: {IP}).",
                    username,
                    remoteEndPoint);
                throw new SecurityException("User is not allowed access at this time.");
            }

            // Update LastActivityDate and LastLoginDate, then save
            if (success)
            {
                if (isUserSession)
                {
                    user.LastActivityDate = user.LastLoginDate = DateTime.UtcNow;
                    await UpdateUserAsync(user).ConfigureAwait(false);
                }

                user.InvalidLoginAttemptCount = 0;
                _logger.LogInformation("Authentication request for {UserName} has succeeded.", user.Username);
            }
            else
            {
                IncrementInvalidLoginAttemptCount(user);
                _logger.LogInformation(
                    "Authentication request for {UserName} has been denied (IP: {IP}).",
                    user.Username,
                    remoteEndPoint);
            }

            return success ? user : null;
        }

        /// <inheritdoc/>
        public async Task<ForgotPasswordResult> StartForgotPasswordProcess(string enteredUsername, bool isInNetwork)
        {
            var user = string.IsNullOrWhiteSpace(enteredUsername) ? null : GetUserByName(enteredUsername);

            var action = ForgotPasswordAction.InNetworkRequired;

            if (user != null && isInNetwork)
            {
                var passwordResetProvider = GetPasswordResetProvider(user);
                return await passwordResetProvider.StartForgotPasswordProcess(user, isInNetwork).ConfigureAwait(false);
            }

            return new ForgotPasswordResult
            {
                Action = action,
                PinFile = string.Empty
            };
        }

        /// <inheritdoc/>
        public async Task<PinRedeemResult> RedeemPasswordResetPin(string pin)
        {
            foreach (var provider in _passwordResetProviders)
            {
                var result = await provider.RedeemPasswordResetPin(pin).ConfigureAwait(false);

                if (result.Success)
                {
                    return result;
                }
            }

            return new PinRedeemResult
            {
                Success = false,
                UsersReset = Array.Empty<string>()
            };
        }

        /// <inheritdoc/>
        public void AddParts(IEnumerable<IAuthenticationProvider> authenticationProviders, IEnumerable<IPasswordResetProvider> passwordResetProviders)
        {
            _authenticationProviders = authenticationProviders.ToArray();
            _passwordResetProviders = passwordResetProviders.ToArray();

            _invalidAuthProvider = _authenticationProviders.OfType<InvalidAuthProvider>().First();
            _defaultAuthenticationProvider = _authenticationProviders.OfType<DefaultAuthenticationProvider>().First();
            _defaultPasswordResetProvider = _passwordResetProviders.OfType<DefaultPasswordResetProvider>().First();
        }

        /// <inheritdoc/>
        public NameIdPair[] GetAuthenticationProviders()
        {
            return _authenticationProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultAuthenticationProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public NameIdPair[] GetPasswordResetProviders()
        {
            return _passwordResetProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultPasswordResetProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public void UpdateConfiguration(Guid userId, UserConfiguration config)
        {
            var user = GetUserById(userId);
            user.SubtitleMode = config.SubtitleMode;
            user.HidePlayedInLatest = config.HidePlayedInLatest;
            user.EnableLocalPassword = config.EnableLocalPassword;
            user.PlayDefaultAudioTrack = config.PlayDefaultAudioTrack;
            user.DisplayCollectionsView = config.DisplayCollectionsView;
            user.DisplayMissingEpisodes = config.DisplayMissingEpisodes;
            user.AudioLanguagePreference = config.AudioLanguagePreference;
            user.RememberAudioSelections = config.RememberAudioSelections;
            user.EnableNextEpisodeAutoPlay = config.EnableNextEpisodeAutoPlay;
            user.RememberSubtitleSelections = config.RememberSubtitleSelections;
            user.SubtitleLanguagePreference = config.SubtitleLanguagePreference;

            user.SetPreference(PreferenceKind.OrderedViews, config.OrderedViews);
            user.SetPreference(PreferenceKind.GroupedFolders, config.GroupedFolders);
            user.SetPreference(PreferenceKind.MyMediaExcludes, config.MyMediaExcludes);
            user.SetPreference(PreferenceKind.LatestItemExcludes, config.LatestItemsExcludes);

            UpdateUser(user);
        }

        /// <inheritdoc/>
        public void UpdatePolicy(Guid userId, UserPolicy policy)
        {
            var user = GetUserById(userId);

            user.MaxParentalAgeRating = policy.MaxParentalRating;
            user.EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess;
            user.RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit;
            user.AuthenticationProviderId = policy.AuthenticationProviderId;
            user.PasswordResetProviderId = policy.PasswordResetProviderId;
            user.InvalidLoginAttemptCount = policy.InvalidLoginAttemptCount;
            user.LoginAttemptsBeforeLockout = policy.LoginAttemptsBeforeLockout == -1
                ? null
                : new int?(policy.LoginAttemptsBeforeLockout);
            user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
            user.SetPermission(PermissionKind.IsHidden, policy.IsHidden);
            user.SetPermission(PermissionKind.IsDisabled, policy.IsDisabled);
            user.SetPermission(PermissionKind.EnableSharedDeviceControl, policy.EnableSharedDeviceControl);
            user.SetPermission(PermissionKind.EnableRemoteAccess, policy.EnableRemoteAccess);
            user.SetPermission(PermissionKind.EnableLiveTvManagement, policy.EnableLiveTvManagement);
            user.SetPermission(PermissionKind.EnableLiveTvAccess, policy.EnableLiveTvAccess);
            user.SetPermission(PermissionKind.EnableMediaPlayback, policy.EnableMediaPlayback);
            user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, policy.EnableAudioPlaybackTranscoding);
            user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, policy.EnableVideoPlaybackTranscoding);
            user.SetPermission(PermissionKind.EnableContentDeletion, policy.EnableContentDeletion);
            user.SetPermission(PermissionKind.EnableContentDownloading, policy.EnableContentDownloading);
            user.SetPermission(PermissionKind.EnableSyncTranscoding, policy.EnableSyncTranscoding);
            user.SetPermission(PermissionKind.EnableMediaConversion, policy.EnableMediaConversion);
            user.SetPermission(PermissionKind.EnableAllChannels, policy.EnableAllChannels);
            user.SetPermission(PermissionKind.EnableAllDevices, policy.EnableAllDevices);
            user.SetPermission(PermissionKind.EnableAllFolders, policy.EnableAllFolders);
            user.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, policy.EnableRemoteControlOfOtherUsers);
            user.SetPermission(PermissionKind.EnablePlaybackRemuxing, policy.EnablePlaybackRemuxing);
            user.SetPermission(PermissionKind.ForceRemoteSourceTranscoding, policy.ForceRemoteSourceTranscoding);
            user.SetPermission(PermissionKind.EnablePublicSharing, policy.EnablePublicSharing);

            user.AccessSchedules.Clear();
            foreach (var policyAccessSchedule in policy.AccessSchedules)
            {
                user.AccessSchedules.Add(policyAccessSchedule);
            }

            user.SetPreference(PreferenceKind.BlockedTags, policy.BlockedTags);
            user.SetPreference(PreferenceKind.EnabledChannels, policy.EnabledChannels);
            user.SetPreference(PreferenceKind.EnabledDevices, policy.EnabledDevices);
            user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders);
            user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, policy.EnableContentDeletionFromFolders);
        }

        private bool IsValidUsername(string name)
        {
            // This is some regex that matches only on unicode "word" characters, as well as -, _ and @
            // In theory this will cut out most if not all 'control' characters which should help minimize any weirdness
            // Usernames can contain letters (a-z + whatever else unicode is cool with), numbers (0-9), at-signs (@), dashes (-), underscores (_), apostrophes ('), and periods (.)
            return Regex.IsMatch(name, @"^[\w\-'._@]*$");
        }

        private IAuthenticationProvider GetAuthenticationProvider(User user)
        {
            return GetAuthenticationProviders(user)[0];
        }

        private IPasswordResetProvider GetPasswordResetProvider(User user)
        {
            return GetPasswordResetProviders(user)[0];
        }

        private IList<IAuthenticationProvider> GetAuthenticationProviders(User user)
        {
            var authenticationProviderId = user?.AuthenticationProviderId;

            var providers = _authenticationProviders.Where(i => i.IsEnabled).ToList();

            if (!string.IsNullOrEmpty(authenticationProviderId))
            {
                providers = providers.Where(i => string.Equals(authenticationProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (providers.Count == 0)
            {
                // Assign the user to the InvalidAuthProvider since no configured auth provider was valid/found
                _logger.LogWarning(
                    "User {Username} was found with invalid/missing Authentication Provider {AuthenticationProviderId}. Assigning user to InvalidAuthProvider until this is corrected",
                    user?.Username,
                    user?.AuthenticationProviderId);
                providers = new List<IAuthenticationProvider>
                {
                    _invalidAuthProvider
                };
            }

            return providers;
        }

        private IList<IPasswordResetProvider> GetPasswordResetProviders(User user)
        {
            var passwordResetProviderId = user?.PasswordResetProviderId;
            var providers = _passwordResetProviders.Where(i => i.IsEnabled).ToArray();

            if (!string.IsNullOrEmpty(passwordResetProviderId))
            {
                providers = providers.Where(i =>
                        string.Equals(passwordResetProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            if (providers.Length == 0)
            {
                providers = new IPasswordResetProvider[]
                {
                    _defaultPasswordResetProvider
                };
            }

            return providers;
        }

        private async Task<(IAuthenticationProvider authenticationProvider, string username, bool success)> AuthenticateLocalUser(
                string username,
                string password,
                User user,
                string remoteEndPoint)
        {
            bool success = false;
            IAuthenticationProvider authenticationProvider = null;

            foreach (var provider in GetAuthenticationProviders(user))
            {
                var providerAuthResult =
                    await AuthenticateWithProvider(provider, username, password, user).ConfigureAwait(false);
                var updatedUsername = providerAuthResult.username;
                success = providerAuthResult.success;

                if (success)
                {
                    authenticationProvider = provider;
                    username = updatedUsername;
                    break;
                }
            }

            if (!success
                && _networkManager.IsInLocalNetwork(remoteEndPoint)
                && user?.EnableLocalPassword == true
                && !string.IsNullOrEmpty(user.EasyPassword))
            {
                // Check easy password
                var passwordHash = PasswordHash.Parse(user.EasyPassword);
                var hash = _cryptoProvider.ComputeHash(
                    passwordHash.Id,
                    Encoding.UTF8.GetBytes(password),
                    passwordHash.Salt.ToArray());
                success = passwordHash.Hash.SequenceEqual(hash);
            }

            return (authenticationProvider, username, success);
        }

        private async Task<(string username, bool success)> AuthenticateWithProvider(
            IAuthenticationProvider provider,
            string username,
            string password,
            User resolvedUser)
        {
            try
            {
                var authenticationResult = provider is IRequiresResolvedUser requiresResolvedUser
                    ? await requiresResolvedUser.Authenticate(username, password, resolvedUser).ConfigureAwait(false)
                    : await provider.Authenticate(username, password).ConfigureAwait(false);

                if (authenticationResult.Username != username)
                {
                    _logger.LogDebug("Authentication provider provided updated username {1}", authenticationResult.Username);
                    username = authenticationResult.Username;
                }

                return (username, true);
            }
            catch (AuthenticationException ex)
            {
                _logger.LogError(ex, "Error authenticating with provider {Provider}", provider.Name);

                return (username, false);
            }
        }

        private void IncrementInvalidLoginAttemptCount(User user)
        {
            int invalidLogins = user.InvalidLoginAttemptCount;
            int? maxInvalidLogins = user.LoginAttemptsBeforeLockout;
            if (maxInvalidLogins.HasValue && invalidLogins >= maxInvalidLogins)
            {
                user.SetPermission(PermissionKind.IsDisabled, true);
                OnUserLockedOut?.Invoke(this, new GenericEventArgs<User>(user));
                _logger.LogWarning(
                    "Disabling user {Username} due to {Attempts} unsuccessful login attempts.",
                    user.Username,
                    invalidLogins);
            }

            UpdateUser(user);
        }
    }
}
