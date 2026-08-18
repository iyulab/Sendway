# Sendway

이메일 · SMS · 앱 푸시 · 슬랙/텔레그램 등 다양한 채널의 메시지 발송을 하나의 API로 통합하는 백엔드 서비스입니다.

## 무엇을 하는가

호출하는 서비스는 발송 대상과 내용만 전달하면 됩니다. 채널별 연동(SMTP, FCM/APNs, 게이트웨이 API 등), 재시도, 발송 이력 관리는 Sendway가 맡습니다.

## 무엇이 아닌가

- 사용자 신원이나 구독 정보를 관리하는 시스템이 아닙니다 — 매 요청마다 수신자 정보를 함께 전달해야 합니다.
- 대량 마케팅 캠페인 발송 도구가 아닙니다 — 트랜잭셔널(이벤트성) 발송에 초점을 둡니다.
- 운영 관제 대시보드를 제공하지 않습니다 — 현재는 API로만 상태를 조회합니다.

## 현재 상태

초기 개발 단계입니다. 안정된 API는 아직 없습니다.

## 시작하는 방법

- **SDK**: C# 라이브러리로 기존 .NET 프로젝트에 직접 포함
- **Docker 서비스**: 독립 실행형 이미지를 관리형 클라우드 인스턴스나 자체 환경에 배포, 언어 무관하게 HTTP API로 호출

```bash
docker build -f src/Sendway.Service/Dockerfile -t sendway-service .
docker run -p 8080:8080 \
  -v sendway-data:/data \
  -e Smtp__Provider=Gmail \
  -e Smtp__Username=... -e Smtp__Password=... -e Smtp__FromAddress=... \
  sendway-service
```

채널 자격증명(암호화됨)과 발송 상태는 `/data`에 저장됩니다. 이 볼륨을 마운트하지 않으면 컨테이너를
새로 만들 때마다 초기화됩니다(재기동 시 위 환경변수 값으로 자동 재시드되므로 서비스 자체는 계속
동작하지만, 그 사이의 발송 이력 조회 결과는 사라집니다).

## 개발

테스트 중 하나(`Sendway.Core.Tests`, `Category=Integration`)는 로컬 SMTP 캐처가 필요합니다:

```bash
docker run -d --name sendway-smtp4dev -p 2525:25 -p 5001:80 rnwood/smtp4dev
dotnet test
```

캐처 없이 실행하려면 `dotnet test --filter "Category!=Integration"`.

## 문서

- [개요](docs/overview.md)
- [원칙](docs/principles.md)
- [범위](docs/scope.md)
- [아키텍처](docs/architecture.md)
- [개념](docs/concepts.md)
- [용어집](docs/glossary.md)

## 라이선스

Apache License 2.0. [LICENSE](LICENSE) 참고.
