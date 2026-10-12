# 아키텍처

상태: 이 문서는 현재 구현을 기술한다

## 배포 모델

현재는 **Docker 서비스** 경로만 존재합니다 — 독립 실행형 이미지로 빌드되어(`src/Sendway.Service/Dockerfile`) 언어에 관계없이 HTTP API로 호출합니다. 관리형 클라우드 인스턴스와 자체 온프레미스 환경 모두에 같은 이미지를 배포할 수 있습니다.

**SDK**(C# 라이브러리로 기존 .NET 프로젝트에 직접 포함하는 경로)는 아직 없습니다 — 계획만 있는 상태입니다.

## 채널

**이메일**과 **앱 푸시** 두 채널이 있습니다.

- 이메일: 기본은 SMTP로 직접 발송합니다([MailKit](https://github.com/jstedfast/MailKit) 사용). 발신 서버는 Gmail/Office365 프리셋이나 직접 지정한 호스트를 씁니다. 테넌트가 `email-graph` 채널 자격증명(Azure AD 앱의 Directory/Client ID·Client Secret·발신 주소, [Microsoft Graph](https://github.com/microsoftgraph/msgraph-sdk-dotnet)의 `Mail.Send` application permission 사용)을 등록하면 그 테넌트의 발송만 SMTP 대신 Graph API로 라우팅됩니다 — SMTP AUTH를 지원하지 않는 M365 사서함을 발신자로 쓰기 위한 경로입니다. Graph 경로는 SMTP의 `multipart/alternative`(plain-text 항상 fallback으로 동봉)와 달리 `htmlBody`가 있으면 HTML만, 없으면 plain-text만 담아 보냅니다 — Graph의 `sendMail`이 본문 하나에 콘텐츠 타입 하나만 허용하기 때문입니다.
- 앱 푸시: Firebase Cloud Messaging으로 발송합니다([FirebaseAdmin](https://github.com/firebase/firebase-admin-dotnet) 사용). APNs(iOS 직접 연동)는 아직 없습니다.

새 채널은 공통 인터페이스를 구현하는 어댑터를 추가하는 방식으로 확장합니다.

## 테넌트/인증

`/messages/*` 호출은 `X-Api-Key` 헤더로 테넌트를 식별합니다 — 테넌트는 Sendway를 호출하는 서비스 하나에 대응합니다(1:1). 키가 없거나 비활성 테넌트의 키면 401을 반환합니다.

테넌트는 `/admin/*` 관리 API로 런타임에 등록합니다(`X-Admin-Key`로 보호되는 별도 관리자 키). 테넌트 생성 시 API 키가 평문으로 한 번만 반환되며, 이후에는 해시만 저장되어 재조회할 수 없습니다. `POST /admin/tenants/{id}/rotate-key`로 키를 교체할 수 있습니다.

## 통합 인터페이스

### 접수

`POST /messages/email`, `POST /messages/push`는 요청을 **접수**하고 바로 응답합니다. 요청을 검증해 데이터베이스에 저장한 뒤 `202 Accepted`와 함께 메시지 ID를 돌려주고(`{"id": ..., "status": "Queued"}`, `Location: /messages/{id}`), 실제 발송은 전달 워커가 뒤이어 수행합니다. 응답 시간은 발송 프로바이더(SMTP·Graph·FCM)의 응답 시간과 무관하며, 접수된 메시지는 데이터베이스에 있으므로 프로세스가 재시작되어도 사라지지 않습니다.

접수 단계에서 알 수 있는 오류는 `400`으로 거절하며, 이때는 아무것도 저장되지 않고 메시지 ID도 없습니다 — 필수 필드 누락, 크기 상한 초과, 형식이 잘못된 이메일 주소가 여기에 해당합니다. 프로바이더만 알 수 있는 오류(등록이 해제된 디바이스 토큰, 존재하지 않는 사서함 등)는 접수된 뒤 그 메시지의 발송 실패로 나타납니다.

### 오류 응답

본문이 있는 오류 응답은 모두 같은 형식([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details, `application/problem+json`)입니다.

```json
{"type": "…", "title": "Bad Request", "status": 400, "detail": "subject must not exceed 200 characters.", "code": "invalid_request"}
```

`code`는 호출자가 분기할 수 있는 값이고 계약의 일부입니다. `detail`은 사람이 읽는 영어 설명이며 문구는 바뀔 수 있으므로 여기에 의존하지 않습니다.

| `code` | 상태 | 의미 |
|---|---|---|
| `invalid_request` | 400 | 요청 자체가 잘못됨(필수 필드 누락, 크기 상한 초과, 쓸 수 없는 값). 그대로 다시 보내도 같은 응답 |
| `invalid_recipient` | 400 | 목적지 주소의 형식이 잘못됨 |
| `idempotency_key_reused` | 422 | 같은 `Idempotency-Key`가 다른 내용의 요청에 이미 쓰임 |
| `rate_limited` | 429 | 테넌트의 요청 한도 초과. `Retry-After` 헤더만큼 기다린 뒤 재시도 |

인증 실패(`401`)와 없는 리소스(`404`)는 본문 없이 상태 코드만 응답합니다.

### 전달과 재시도

전달 워커는 접수된 메시지를 꺼내 한 번 발송을 시도하고 결과를 기록합니다. 시도가 실패하면 간격을 늘려가며 다시 시도합니다 — 기본값으로 최대 5회, 실패 후 5초·30초·3분·18분 뒤입니다. 한 번의 시도는 30초 안에 끝나야 하며, 그 안에 프로바이더가 응답하지 않으면 그 시도는 실패로 치고 다음 시도로 넘어갑니다. 목적지 자체가 거부된 경우는 다시 시도해도 결과가 같으므로 재시도 없이 실패로 확정합니다. 모든 시도가 실패하면 메시지는 `Failed`가 됩니다.

전달 보장은 **최소 한 번**입니다. 프로바이더가 메시지를 받아들인 직후, 그 결과를 기록하기 전에 프로세스가 죽으면 같은 메시지가 한 번 더 나갈 수 있습니다. 정상 종료(재배포 등) 중이던 시도는 횟수에 넣지 않고 대기열로 되돌립니다.

시도 횟수·간격·시도당 제한 시간·동시 전달 수는 `Sendway:Delivery:*` 설정(`MaxAttempts`, `RetryBaseDelay`, `RetryBackoffFactor`, `AttemptTimeout`, `Concurrency`, `PollInterval`)으로 바꿀 수 있습니다. 같은 데이터베이스를 보는 인스턴스가 여럿이어도 한 메시지의 한 시도는 한 인스턴스만 가져갑니다. `Sendway:Delivery:WorkerEnabled`를 `false`로 두면 그 인스턴스는 접수만 하고 전달은 다른 인스턴스에 맡깁니다.

### 상태 조회

`GET /messages/{id}`는 그 메시지의 현재 상태를 돌려줍니다(요청한 테넌트가 보낸 메시지만 조회 가능).

| 필드 | 의미 |
|---|---|
| `status` | `Queued`(발송 대기 — 첫 시도 전이거나 재시도를 기다리는 중), `Sending`(시도 진행 중), `Sent`(발송됨), `Failed`(실패로 확정) |
| `error` | 가장 최근 시도의 오류. 재시도를 기다리는 `Queued` 상태에서도 보입니다 |
| `attempts` | 지금까지의 시도 횟수 |
| `acceptedAt` / `completedAt` | 접수 시각 / `Sent`·`Failed`가 된 시각(그 전에는 `null`) |
| `channel`, `recipient` | 채널과 목적지 |

### 결과 통지(웹훅)

테넌트에 웹훅 엔드포인트를 등록해 두면, 그 테넌트의 메시지가 `Sent`나 `Failed`로 끝날 때마다 그 주소로 이벤트를 `POST`합니다. 재시도를 기다리는 중간 실패는 알리지 않습니다 — 최종 결과만 알립니다.

- 등록: `PUT /admin/tenants/{id}/webhook`에 `{"url": "https://..."}`. 응답에 서명 비밀값(`whsec_...`)이 실려 오며 이때 한 번만 볼 수 있습니다. 다시 등록하면 주소와 비밀값이 함께 바뀝니다. `GET`으로 등록된 주소를 확인하고 `DELETE`로 해제합니다. 테넌트당 하나입니다.
- 본문: `{"type": "message.sent" | "message.failed", "timestamp": "...", "data": { ... }}`. `data`는 그 메시지에 대해 상태 조회가 돌려주는 것과 같은 문서입니다.
- 서명: 요청은 [Standard Webhooks](https://www.standardwebhooks.com/) 규격을 따릅니다. `webhook-id`(이벤트 ID), `webhook-timestamp`(초 단위 유닉스 시각), `webhook-signature`(`v1,` 뒤에 `{id}.{timestamp}.{본문}`을 비밀값으로 HMAC-SHA256 서명한 값) 헤더가 붙으므로, 이 규격의 기존 검증 라이브러리를 그대로 쓸 수 있습니다.
- 응답: 수신 측이 `2xx`로 답하면 전달된 것으로 봅니다. 그 외의 응답이나 15초 안에 응답이 없으면 간격을 늘려가며 다시 보냅니다(기본값으로 최대 6회 — 5초·30초·3분·18분·1시간 48분 뒤). `410 Gone`은 더 받지 않겠다는 뜻으로 보고 그 이벤트는 다시 보내지 않습니다. 리다이렉트는 따라가지 않습니다.
- 중복: 전달 보장은 최소 한 번입니다. 같은 이벤트가 다시 올 때 `webhook-id`는 같으므로 수신 측은 이 값으로 중복을 걸러냅니다.

이벤트는 발송 결과를 기록하는 것과 같은 트랜잭션에서 대기열에 들어가므로, 결과는 기록됐는데 이벤트가 빠지는 경우는 없습니다. 모든 시도가 실패해 이벤트가 끝내 전달되지 않아도 결과 자체는 상태 조회로 확인할 수 있습니다. 시도 횟수·간격·제한 시간은 `Sendway:Webhooks:*` 설정(`MaxAttempts`, `RetryBaseDelay`, `RetryBackoffFactor`, `AttemptTimeout`, `Concurrency`)으로 바꿉니다.

### 중복 발송 방지

요청에 `Idempotency-Key` 헤더(최대 255자)를 실으면, 같은 테넌트가 같은 키로 다시 요청했을 때 새 메시지를 만들지 않고 처음 접수된 메시지의 ID로 똑같이 `202`를 응답합니다(본문의 `status`는 그 메시지의 현재 상태). 첫 요청의 발송이 아직 진행 중이거나 재시도를 기다리는 중이어도 마찬가지입니다 — 응답을 받지 못한 호출자가 같은 키로 다시 요청해도 메시지는 한 번만 나갑니다. 같은 키에 다른 내용을 실어 보내면 `422`로 거절합니다. 키는 접수 후 24시간 동안 유효합니다.

이 보장은 데이터베이스의 유일성 제약으로 지켜지므로 재시작 후에도, 인스턴스가 여럿이어도 성립합니다.

### 요청 형식과 상한

테넌트별로 분당 요청 수 상한이 있으며(같은 키의 재요청도 한 건으로 셉니다)(초과 시 429 + `Retry-After`), 여러 테넌트가 공유하는 업스트림 자격증명을 한 테넌트의 과다 요청으로부터 보호합니다.

이메일은 `to`(최소 1개)에 더해 선택적으로 `cc`/`bcc`를 리스트로 받아 한 번의 호출로 여러 수신자에게 보낼 수 있습니다 — SMTP 봉투 하나에 모두 실려 발송되며(전체가 한 단위로 성공/실패), 발송 상태 조회 결과의 `recipient` 필드는 `to`(콤마 구분)에 `cc`/`bcc`가 있으면 `; cc: ...`/`; bcc: ...`를 이어붙인 요약 문자열입니다.

이메일은 필수인 `body`(plain-text) 위에 선택적으로 `htmlBody`를 실을 수 있습니다 — 지정하면 `multipart/alternative`(plain-text가 fallback 파트, HTML이 선호 파트)로 발송되고, 생략하면 기존과 동일하게 plain-text 단일 파트로 발송됩니다. 템플릿 렌더링(변수 치환 등)은 하지 않습니다 — 범위 밖이며, 호출자가 완성된 HTML 문자열을 그대로 넘깁니다.

요청 크기에는 상한이 있습니다(이메일: 주소 1개당 320자(`to`/`cc`/`bcc` 공통)·수신자 총합(`to`+`cc`+`bcc`) 1,000명·제목 200자·본문(`body`/`htmlBody` 각각) 100만자, 앱 푸시: 디바이스 토큰 4096자·제목 200자·본문 4000자) — 초과하면 400을 반환합니다.

## 운영

`GET /admin/queues`(관리자 키 필요)는 메시지 대기열과 웹훅 이벤트 대기열 각각에 대해 대기 중(`queued`)·시도 중(`sending`) 건수, 최근 24시간 동안 실패로 확정된 건수(`failedInLast24Hours`), 그리고 `oldestDueSeconds`를 돌려줍니다. `oldestDueSeconds`는 지금 가져갈 수 있는 항목 가운데 가장 오래 기다린 것의 대기 시간으로, 전달이 정상이면 0에 가깝거나 `null`이고 워커가 멈췄거나 밀리면 계속 커집니다 — 경보를 걸기에 알맞은 값입니다. 재시도 시각을 기다리는 항목은 `queued`에는 들어가지만 이 값에는 들어가지 않습니다.

요청마다 처리 시간이 로그에 남습니다(`Request finished … 12.3ms`). 호출하는 쪽에서 잰 응답 시간과 비교하면 지연이 서비스 안에서 생겼는지 그 앞(프록시·네트워크)에서 생겼는지 가를 수 있습니다.

## 저장

채널 자격증명, 메시지(접수된 내용과 발송 상태), 웹훅 엔드포인트와 전달 대기 중인 이벤트는 PostgreSQL 데이터베이스에 저장됩니다(`ConnectionStrings:Sendway`). 웹훅 서명 비밀값은 채널 자격증명과 같은 방식으로 암호화해 둡니다. 메시지 내용은 발송에 필요한 동안만 암호화해 보관하고, 메시지가 `Sent`나 `Failed`로 끝나면 지웁니다 — 그 뒤에는 채널·목적지·상태·시각만 남습니다. 채널 자격증명은 **기본적으로 프로세스 시작 시 설정(환경변수 또는 설정 파일)으로 공급된 공용 값**을 쓰며, 테넌트별로 `PUT /admin/tenants/{id}/credentials/{channel}`을 통해 오버라이드를 등록할 수 있습니다(예: 테넌트마다 다른 Firebase 프로젝트). 등록할 때 그 채널의 발송에 꼭 필요한 항목이 빠져 있으면(발신 주소, SMTP 호스트나 프리셋, Graph 앱 정보, 서비스 계정 키 등) 저장하지 않고 `400`으로 이유를 돌려줍니다 — 프로바이더가 그 자격증명을 실제로 받아 주는지는 발송해 봐야 알 수 있습니다. 등록한 오버라이드는 `DELETE /admin/tenants/{id}/credentials/{channel}`로 제거할 수 있으며, 제거 후에는 공용 기본값(없으면 미설정)으로 되돌아갑니다. 두 경우 모두 [ASP.NET Core Data Protection API](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction)로 암호화한 뒤 기록합니다 — 특정 클라우드 벤더의 키 관리 서비스에 의존하지 않으므로 클라우드/온프레미스 배포 모두에서 동일하게 동작합니다. 암호화 키 자체는 별도 디렉터리(컨테이너 배포 시 `/data/dp-keys`, 볼륨 마운트 필요)에 파일로 보관됩니다. (테스트 스위트는 격리를 위해 별도로 SQLite 임시 파일을 씁니다 — 실행 중인 서비스와는 무관.)

내장 파일 DB(SQLite)를 컨테이너 배포에서 쓰지 않는 이유: SQLite는 EF Core 기본 WAL 저널 모드가 요구하는 파일 락을 네트워크 마운트 볼륨(Azure Files 등)에서 신뢰성 있게 지원하지 않는다 — 재현 시 `SQLite Error 5: database is locked`.
