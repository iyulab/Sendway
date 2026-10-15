# Changelog

## 0.1.0 — unreleased

- `SendwayClient`: send email, SMS and app push (with an optional `Idempotency-Key` and `ExpiresAt`), and read a
  message's status and outcome.
- `Message.Reason`: why a message ended `Failed` (`RecipientRefused`, `MessageRefused`, `RecipientRateLimited`,
  `Expired`, `AttemptsExhausted`); unknown values read as `Unknown` with the text kept.
- `SendwayException`: a refused call's HTTP status, problem `code` and `Retry-After`.
- `SendwayWebhook`: verifies an outcome webhook's Standard Webhooks signature and timestamp, and reads the event.
