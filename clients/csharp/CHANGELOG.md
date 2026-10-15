# Changelog

## 0.1.0 — 2026-10-15

- `SendwayClient`: send email, SMS and app push (with an optional `Idempotency-Key` and `ExpiresAt`), and read a
  message's status and outcome.
- `Message.Reason`: why a message ended `Failed` (`RecipientRefused`, `MessageRefused`, `RecipientRateLimited`,
  `Expired`, `AttemptsExhausted`); unknown values read as `Unknown` with the text kept.
- `Message.RefusedRecipients`: email recipients the mail server refused for good while the others received the
  message.
- `SendwayException`: a refused call's HTTP status, problem `code` (including `DestinationNotAllowed` and
  `DailyLimitReached`) and `Retry-After`.
- `SendwayWebhook`: verifies an outcome webhook's Standard Webhooks signature and timestamp, and reads the event.
