namespace Sendway.Core;

// Queued and Sending are transient; Sent and Failed are terminal. A message goes back from Sending
// to Queued when an attempt fails transiently and a later attempt is scheduled.
public enum MessageDeliveryStatus
{
    Queued,
    Sending,
    Sent,
    Failed
}
