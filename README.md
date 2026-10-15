# Sendway

[![CI](https://github.com/iyulab/Sendway/actions/workflows/ci.yml/badge.svg)](https://github.com/iyulab/Sendway/actions/workflows/ci.yml)

이메일 · SMS · 앱 푸시 메시지 발송을 하나의 HTTP API로 통합하는 백엔드 서비스입니다. 다른 채널(슬랙·텔레그램 등)은 필요가 생기면 어댑터로 붙입니다.

## 무엇을 하는가

호출하는 서비스는 발송 대상과 내용만 전달하면 됩니다. 채널별 연동(SMTP·Microsoft Graph, Firebase Cloud Messaging, SMS 게이트웨이), 재시도, 발송 이력 관리는 Sendway가 맡습니다.

## 무엇이 아닌가

- 사용자 신원이나 구독 정보를 관리하는 시스템이 아닙니다 — 매 요청마다 수신자 정보를 함께 전달해야 합니다.
- 대량 마케팅 캠페인 발송 도구가 아닙니다 — 트랜잭셔널(이벤트성) 발송에 초점을 둡니다. 광고성 문자(수신거부 번호·야간 제한 같은 광고 규정)도 다루지 않습니다.
- 운영 관제 대시보드를 제공하지 않습니다 — 현재는 API로만 상태를 조회합니다.

## 현재 상태

초기 개발 단계입니다. 안정된 API는 아직 없습니다.

## 시작하는 방법

독립 실행형 이미지를 관리형 클라우드 인스턴스나 자체 환경에 배포하고, 언어와 무관하게 HTTP API로 호출합니다. 프로젝트에 라이브러리로 포함하는 경로는 없습니다. .NET에서는 HTTP API를 감싼 클라이언트 [`Sendway.Client`](clients/csharp/README.md)(NuGet)를 쓸 수 있고, 다른 언어는 [OpenAPI 기술](docs/openapi.json)로 클라이언트를 만들 수 있습니다.

```bash
docker build -f src/Sendway.Service/Dockerfile -t sendway-service .
docker run -p 8080:8080 \
  -v sendway-data:/data \
  -e Sendway__DatabaseProvider=Sqlite \
  -e ConnectionStrings__Sendway="Data Source=/data/sendway.db" \
  -e Smtp__Provider=Gmail \
  -e Smtp__Username=... -e Smtp__Password=... -e Smtp__FromAddress=... \
  sendway-service
```

위 예시는 별도 데이터베이스 없이 로컬에서 바로 띄워보기 위한 SQLite 경로입니다(테스트 스위트의 기본 저장소도 SQLite입니다) — SQLite는 파일이
네트워크 마운트 볼륨(클라우드 스토리지 등)에 있으면 안정적으로 동작하지 않으므로, 실제 배포에서는
`ConnectionStrings__Sendway`에 PostgreSQL 연결 문자열을 넣고 `Sendway__DatabaseProvider`는 생략(기본값이
PostgreSQL)합니다. 실제로 운영할 때의 설정·업그레이드·백업·장애 대응은 [운영](docs/operations.md)에 있습니다. 채널 자격증명(암호화됨)과 Data Protection 키 링은 `/data`에 저장됩니다 — 이 볼륨을
마운트하지 않으면 컨테이너를 새로 만들 때마다 초기화됩니다(공용 자격증명은 시작할 때마다 위 환경변수
값으로 다시 저장되므로 서비스 자체는 계속 동작하지만, 그 사이의 발송 이력 조회 결과는 사라집니다).

## 보내 보기

관리자 키(`Sendway__AdminApiKey` 환경변수로 지정)로 테넌트를 만들고, 받은 API 키로 발송합니다.

```bash
# 테넌트 생성 — apiKey는 이 응답에서만 볼 수 있습니다
curl -X POST http://localhost:8080/admin/tenants \
  -H "X-Admin-Key: <관리자 키>" -H "Content-Type: application/json" \
  -d '{"name": "my-service"}'

# 발송 요청 — 접수되면 202와 메시지 ID를 바로 돌려줍니다
curl -X POST http://localhost:8080/messages/email \
  -H "X-Api-Key: <apiKey>" -H "Content-Type: application/json" \
  -H "Idempotency-Key: <요청마다 고유한 값>" \
  -d '{"to": ["user@example.com"], "subject": "Hello", "body": "Hello from Sendway"}'
# → 202 {"id": "…", "status": "Queued"}

# 발송 결과 확인
curl http://localhost:8080/messages/<id> -H "X-Api-Key: <apiKey>"
# → {"status": "Sent", "attempts": 1, …}
```

발송은 요청과 따로 진행되며, 실패하면 Sendway가 간격을 두고 다시 시도합니다. 같은 `Idempotency-Key`로 다시 요청하면 메시지를 새로 만들지 않고 같은 ID를 돌려주므로, 응답을 받지 못했을 때 안심하고 재요청할 수 있습니다. 결과를 조회하는 대신 웹훅으로 받을 수도 있습니다 — [아키텍처](docs/architecture.md) 참고.

## 개발

테스트 중 하나(`Sendway.Core.Tests`, `Category=Integration`)는 로컬 SMTP 캐처가 필요합니다:

```bash
docker run -d --name sendway-smtp4dev -p 2525:25 -p 5001:80 rnwood/smtp4dev
dotnet test
```

캐처 없이 실행하려면 `dotnet test --filter "Category!=Integration"`. CI는 push와 풀 리퀘스트마다 캐처를 함께 띄워 전체 테스트를 실행합니다.

저장소 계층 테스트는 기본으로 SQLite 임시 파일을 씁니다. 실제 PostgreSQL에서 돌리려면 서버 연결 문자열을 환경변수로 줍니다(테스트가 임시 데이터베이스를 만들고 끝나면 지웁니다):

```bash
docker run -d --name sendway-test-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17
SENDWAY_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres" dotnet test tests/Sendway.Core.Tests
```

## 문서

- [개요](docs/overview.md)
- [원칙](docs/principles.md)
- [범위](docs/scope.md)
- [아키텍처](docs/architecture.md)
- [API 기술(OpenAPI 3.1)](docs/openapi.json)
- [운영](docs/operations.md)
- [개념](docs/concepts.md)
- [용어집](docs/glossary.md)

## 라이선스

Apache License 2.0. [LICENSE](LICENSE) 참고.
