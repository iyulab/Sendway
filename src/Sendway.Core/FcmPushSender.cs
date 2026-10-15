using System.Collections.Concurrent;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace Sendway.Core;

public sealed class FcmPushSender : IPushSender
{
    // FCM keeps an undelivered message for at most 28 days.
    private static readonly TimeSpan MaxTimeToLive = TimeSpan.FromDays(28);

    private readonly ICredentialStore _credentialStore;
    private readonly TimeProvider _timeProvider;

    // Tenants can each use their own FCM project credential (an override), so caching a single
    // FirebaseApp per process would reuse the first tenant's credential for every later tenant.
    // One Lazy<Task<FirebaseApp>> is cached per tenant and created on that tenant's first send.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<FirebaseApp>>> _apps = new();

    public FcmPushSender(ICredentialStore credentialStore, TimeProvider? timeProvider = null)
    {
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        var lazyApp = _apps.GetOrAdd(
            message.TenantId,
            tenantId => new Lazy<Task<FirebaseApp>>(
                () => CreateAppAsync(tenantId, _credentialStore),
                LazyThreadSafetyMode.ExecutionAndPublication));

        FirebaseApp app;
        try
        {
            app = await lazyApp.Value;
        }
        catch
        {
            // A faulted creation (e.g. missing/invalid FCM credential) must not stay cached
            // forever — remove it so the next send retries instead of rethrowing the same stale
            // failure until process restart. Remove only the exact entry we saw fail, in case a
            // concurrent caller already replaced it (e.g. via InvalidateTenant below).
            _apps.TryRemove(new KeyValuePair<Guid, Lazy<Task<FirebaseApp>>>(message.TenantId, lazyApp));
            throw;
        }

        var fcmMessage = Build(message, _timeProvider.GetUtcNow());

        try
        {
            await FirebaseMessaging.GetMessaging(app).SendAsync(fcmMessage, cancellationToken);
        }
        catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
        {
            throw new InvalidRecipientException($"FCM rejected the device token: {ex.MessagingErrorCode}", ex);
        }
    }

    // A message with a deadline tells both platforms to drop it if it is still undelivered then:
    // Android through the time to live, iOS through the APNs expiration header.
    internal static Message Build(PushMessage message, DateTimeOffset now)
    {
        var fcmMessage = new Message
        {
            // Message.Token: FCM is moving to fid, but registration tokens keep working through the
            // migration period and most clients are still issued a token rather than a fid, so this
            // stays on Token until a switch to Fid can be verified against real devices.
#pragma warning disable CS0618
            Token = message.DeviceToken,
#pragma warning restore CS0618
            Notification = new Notification
            {
                Title = message.Title,
                Body = message.Body
            }
        };

        if (message.ExpiresAt is { } expiresAt)
        {
            var left = expiresAt - now;
            fcmMessage.Android = new AndroidConfig
            {
                TimeToLive = left < TimeSpan.Zero ? TimeSpan.Zero : left > MaxTimeToLive ? MaxTimeToLive : left
            };
            fcmMessage.Apns = new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-expiration"] = expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            };
        }

        return fcmMessage;
    }

    // Called after a tenant's FCM credential is set, replaced or removed, so the cached
    // FirebaseApp doesn't keep using the old one. It is recreated on the next send.
    public void InvalidateTenant(Guid tenantId)
    {
        _apps.TryRemove(tenantId, out _);
    }

    // Called after the shared FCM credential changes. Every tenant without an override built its
    // cached FirebaseApp from the shared credential, so all of them are recreated on their next send.
    public void InvalidateAll()
    {
        _apps.Clear();
    }

    private static async Task<FirebaseApp> CreateAppAsync(Guid tenantId, ICredentialStore credentialStore)
    {
        var options = await credentialStore.GetAsync<FcmOptions>(tenantId, ChannelCredentialNames.Fcm)
            ?? throw new InvalidOperationException("Fcm channel credentials have not been configured.");

        if (string.IsNullOrWhiteSpace(options.CredentialsJson))
        {
            throw new InvalidOperationException("FcmOptions.CredentialsJson is required.");
        }

        // GoogleCredential.FromJson: the credential is supplied by the operator through the
        // encrypted store, not taken from an untrusted source, which is the case the deprecation
        // warns about. Moving to CredentialFactory waits until it can be verified with a real key.
#pragma warning disable CS0618
        var credential = GoogleCredential.FromJson(options.CredentialsJson);
#pragma warning restore CS0618
        return FirebaseApp.Create(new AppOptions { Credential = credential }, Guid.NewGuid().ToString());
    }
}
