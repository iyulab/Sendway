using System.Net;

namespace Sendway.Client;

/// <summary>A call Sendway answered with an error (RFC 9457 problem details).</summary>
public sealed class SendwayException : Exception
{
    public SendwayException()
    {
    }

    public SendwayException(string message)
        : base(message)
    {
    }

    public SendwayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SendwayException(HttpStatusCode status, string message, string? codeText, TimeSpan? retryAfter)
        : base(message)
    {
        Status = status;
        CodeText = codeText;
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status Sendway answered with.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Why the call was refused, for code to branch on.</summary>
    public SendwayErrorCode Code => Names.Code(CodeText);

    /// <summary>The problem's <c>code</c> as sent; null when the answer carried none (something in front of Sendway answered).</summary>
    public string? CodeText { get; }

    /// <summary>How long to wait before calling again, when Sendway said (<see cref="SendwayErrorCode.RateLimited"/>).</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Why Sendway refused a call.</summary>
public enum SendwayErrorCode
{
    /// <summary>A code this version of the client does not know, or none; <see cref="SendwayException.CodeText"/> has it as sent.</summary>
    Unknown,

    /// <summary>400 — the request is malformed or misses something; the message says what. Do not retry as is.</summary>
    InvalidRequest,

    /// <summary>400 — the destination is malformed (an address, or a number not in E.164 form). Do not retry as is.</summary>
    InvalidRecipient,

    /// <summary>422 — the <c>Idempotency-Key</c> was already used for a different request.</summary>
    IdempotencyKeyReused,

    /// <summary>409 — nothing can be sent on this channel for this service yet. Nothing was stored; do not retry as is.</summary>
    ChannelNotConfigured,

    /// <summary>429 — over the service's request limit. Retry after <see cref="SendwayException.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>401 — the API key is missing or not valid.</summary>
    Unauthorized,

    /// <summary>404 — no such message for this service.</summary>
    NotFound,

    /// <summary>500 — something failed inside Sendway. Retrying with the same <c>Idempotency-Key</c> is safe.</summary>
    InternalError
}
