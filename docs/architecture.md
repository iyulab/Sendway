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

`POST /messages/email`, `POST /messages/push` 호출은 **동기적으로** 처리됩니다 — 발송이 끝날 때까지 기다렸다가 성공/실패를 그 자리에서 응답합니다. 업스트림(SMTP/FCM)이 일시적으로 실패하면 지수 백오프로 최대 3회까지 자동 재시도한 뒤 최종 실패로 확정합니다 — 이 재시도는 요청 하나의 처리 범위 안에서만 일어나며, 프로세스가 재시작되면(재배포 등) 진행 중이던 재시도는 유실됩니다. 재시작에도 살아남는 재시도(큐 기반)는 아직 없습니다.

요청에 `Idempotency-Key` 헤더(최대 255자)를 실으면, 같은 테넌트가 같은 키로 재요청했을 때 다시 보내지 않고 이전 응답을 그대로 반환합니다(성공·확정적 실패는 재생하고, 결과가 불확실한 일시적 실패는 캐시하지 않아 같은 키로도 다시 시도됩니다). 이 캐시는 프로세스 메모리에만 있어 재시작 시 비워집니다.

테넌트별로 분당 요청 수 상한이 있으며(초과 시 429 + `Retry-After`), 여러 테넌트가 공유하는 업스트림 자격증명을 한 테넌트의 과다 요청으로부터 보호합니다.

이메일은 `to`(최소 1개)에 더해 선택적으로 `cc`/`bcc`를 리스트로 받아 한 번의 호출로 여러 수신자에게 보낼 수 있습니다 — SMTP 봉투 하나에 모두 실려 발송되며(전체가 한 단위로 성공/실패), 발송 상태 조회 결과의 `recipient` 필드는 `to`(콤마 구분)에 `cc`/`bcc`가 있으면 `; cc: ...`/`; bcc: ...`를 이어붙인 요약 문자열입니다.

이메일은 필수인 `body`(plain-text) 위에 선택적으로 `htmlBody`를 실을 수 있습니다 — 지정하면 `multipart/alternative`(plain-text가 fallback 파트, HTML이 선호 파트)로 발송되고, 생략하면 기존과 동일하게 plain-text 단일 파트로 발송됩니다. 서버사이드 템플릿 렌더링(변수 치환 등)은 아직 없습니다 — 호출자가 완성된 HTML 문자열을 그대로 넘깁니다.

요청 크기에는 상한이 있습니다(이메일: 주소 1개당 320자(`to`/`cc`/`bcc` 공통)·수신자 총합(`to`+`cc`+`bcc`) 1,000명·제목 200자·본문(`body`/`htmlBody` 각각) 100만자, 앱 푸시: 디바이스 토큰 4096자·제목 200자·본문 4000자) — 초과하면 400을 반환합니다.

성공(200)·실패(400/502) 모두 응답에 메시지 ID가 포함되며, `GET /messages/{id}`로 그 발송 건의 상태(채널·수신자·성공 여부·오류 메시지·발송 시각)를 나중에 다시 조회할 수 있습니다(pull 방식, 요청한 테넌트가 발신한 메시지만 조회 가능). 큐 기반 재시도, 웹훅 등 push 방식의 상태 통지는 아직 없습니다.

## 저장

채널 자격증명과 발송 상태는 PostgreSQL 데이터베이스에 저장됩니다(`ConnectionStrings:Sendway`). 채널 자격증명은 **기본적으로 프로세스 시작 시 설정(환경변수 또는 설정 파일)으로 공급된 공용 값**을 쓰며, 테넌트별로 `PUT /admin/tenants/{id}/credentials/{channel}`을 통해 오버라이드를 등록할 수 있습니다(예: 테넌트마다 다른 Firebase 프로젝트). 두 경우 모두 [ASP.NET Core Data Protection API](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction)로 암호화한 뒤 기록합니다 — 특정 클라우드 벤더의 키 관리 서비스에 의존하지 않으므로 클라우드/온프레미스 배포 모두에서 동일하게 동작합니다. 암호화 키 자체는 별도 디렉터리(컨테이너 배포 시 `/data/dp-keys`, 볼륨 마운트 필요)에 파일로 보관됩니다. (테스트 스위트는 격리를 위해 별도로 SQLite 임시 파일을 씁니다 — 실행 중인 서비스와는 무관.) 메시지 템플릿 저장소는 아직 없습니다.

내장 파일 DB(SQLite)를 컨테이너 배포에서 쓰지 않는 이유: SQLite는 EF Core 기본 WAL 저널 모드가 요구하는 파일 락을 네트워크 마운트 볼륨(Azure Files 등)에서 신뢰성 있게 지원하지 않는다 — 재현 시 `SQLite Error 5: database is locked`.
