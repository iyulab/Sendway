using System.Collections.Concurrent;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace Sendway.Core;

public sealed class FcmPushSender : IPushSender
{
    private readonly ICredentialStore _credentialStore;

    // 테넌트마다 다른 FCM 프로젝트 자격증명(오버라이드)을 쓸 수 있어, FirebaseApp을 프로세스당
    // 하나로 캐시하면 두 번째 테넌트부터 첫 테넌트의 credential이 잘못 재사용된다. 테넌트별로
    // Lazy<Task<FirebaseApp>>를 따로 캐시해 각 테넌트당 최초 1회만 생성한다.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<FirebaseApp>>> _apps = new();

    public FcmPushSender(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        return RetryPolicy.ExecuteAsync(async () =>
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

            var fcmMessage = new Message
            {
                // Message.Token: FCM이 fid로 전환 중이나(2026-06~), token은 마이그레이션 기간 동안
                // 계속 동작하고 하위호환됨. 대부분의 실제 클라이언트가 아직 fid가 아닌 registration
                // token을 발급받으므로, 실제 검증 없이 지금 Fid로 옮기지 않음.
#pragma warning disable CS0618
                Token = message.DeviceToken,
#pragma warning restore CS0618
                Notification = new Notification
                {
                    Title = message.Title,
                    Body = message.Body
                }
            };

            try
            {
                await FirebaseMessaging.GetMessaging(app).SendAsync(fcmMessage, cancellationToken);
            }
            catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
            {
                throw new InvalidRecipientException($"FCM rejected the device token: {ex.MessagingErrorCode}", ex);
            }
        }, cancellationToken: cancellationToken);
    }

    // Task 5(SetTenantCredentialEndpoint)가 이 테넌트의 FCM 자격증명을 새로 등록/교체한 뒤 호출한다
    // — 캐시된 FirebaseApp이 옛 자격증명을 계속 쓰는 걸 막기 위함. 다음 발송 시 재생성됨.
    public void InvalidateTenant(Guid tenantId)
    {
        _apps.TryRemove(tenantId, out _);
    }

    private static async Task<FirebaseApp> CreateAppAsync(Guid tenantId, ICredentialStore credentialStore)
    {
        var options = await credentialStore.GetAsync<FcmOptions>(tenantId, ChannelCredentialNames.Fcm)
            ?? throw new InvalidOperationException("Fcm channel credentials have not been configured.");

        if (string.IsNullOrWhiteSpace(options.CredentialsJson))
        {
            throw new InvalidOperationException("FcmOptions.CredentialsJson is required.");
        }

        // GoogleCredential.FromJson: 배포자가 암호화 저장소를 통해 직접 공급하는 credential이라
        // "외부 소스 미검증" 위험 시나리오에 해당하지 않음. CredentialFactory 마이그레이션은
        // 실제 자격증명으로 검증 가능해지면 진행.
#pragma warning disable CS0618
        var credential = GoogleCredential.FromJson(options.CredentialsJson);
#pragma warning restore CS0618
        return FirebaseApp.Create(new AppOptions { Credential = credential }, Guid.NewGuid().ToString());
    }
}
