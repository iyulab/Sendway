using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace Sendway.Core;

public sealed class FcmPushSender : IPushSender
{
    private readonly Lazy<Task<FirebaseApp>> _app;

    public FcmPushSender(ICredentialStore credentialStore)
    {
        // Lazy<Task<T>>: FirebaseApp은 프로세스 생애주기 동안 정확히 한 번만 생성되고
        // dispose되지 않는다 — Program.cs가 IPushSender를 singleton으로 등록하는 한
        // (인스턴스가 프로세스당 하나) 안전하다. scoped/transient로 바뀌면 FirebaseApp이
        // 인스턴스마다 누적된다. credential은 이제 ICredentialStore(암호화 저장소)에서
        // 비동기로 읽어와야 해서 생성자에서 동기적으로 만들 수 없어 지연 초기화한다.
        _app = new Lazy<Task<FirebaseApp>>(
            () => CreateAppAsync(credentialStore),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static async Task<FirebaseApp> CreateAppAsync(ICredentialStore credentialStore)
    {
        var options = await credentialStore.GetAsync<FcmOptions>(ChannelCredentialNames.Fcm)
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

    public async Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        var app = await _app.Value;

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
    }
}
