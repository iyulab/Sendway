namespace Sendway.Core;

public static class MessageChannels
{
    public const string Email = "email";
    public const string Push = "push";
    public const string Sms = "sms";

    // The channel credentials any one of which lets a tenant send on the channel — set for the tenant
    // or shared. Email goes through Graph when the tenant has a Graph credential, through SMTP otherwise.
    public static IReadOnlyList<string> CredentialsFor(string channel) => channel switch
    {
        Email => [ChannelCredentialNames.EmailGraph, ChannelCredentialNames.Smtp],
        Push => [ChannelCredentialNames.Fcm],
        Sms => [ChannelCredentialNames.Solapi],
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown message channel.")
    };
}
