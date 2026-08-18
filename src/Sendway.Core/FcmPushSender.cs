using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

public sealed class FcmPushSender : IPushSender
{
    private readonly FirebaseApp _app;

    public FcmPushSender(IOptions<FcmOptions> options)
    {
        // GoogleCredential.FromJson: 배포자가 환경변수로 직접 공급하는 credential이라
        // "외부 소스 미검증" 위험 시나리오에 해당하지 않음. CredentialFactory 마이그레이션은
        // 실제 자격증명으로 검증 가능해지면 진행(추적: HANDOFF.md).
#pragma warning disable CS0618
        var credential = GoogleCredential.FromJson(options.Value.CredentialsJson);
#pragma warning restore CS0618
        _app = FirebaseApp.Create(new AppOptions { Credential = credential }, Guid.NewGuid().ToString());
    }

    public async Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
    {
        var fcmMessage = new Message
        {
            // Message.Token: FCM이 fid로 전환 중이나(2026-06~), token은 마이그레이션 기간 동안
            // 계속 동작하고 하위호환됨. 대부분의 실제 클라이언트가 아직 fid가 아닌 registration
            // token을 발급받으므로, 실제 검증 없이 지금 Fid로 옮기지 않음(추적: HANDOFF.md).
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
            await FirebaseMessaging.GetMessaging(_app).SendAsync(fcmMessage, cancellationToken);
        }
        catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
        {
            throw new InvalidRecipientException($"FCM rejected the device token: {ex.MessagingErrorCode}", ex);
        }
    }
}
