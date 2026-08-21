# 아키텍처

상태: 이 문서는 현재 구현을 기술한다

## 배포 모델

현재는 **Docker 서비스** 경로만 존재합니다 — 독립 실행형 이미지로 빌드되어(`src/Sendway.Service/Dockerfile`) 언어에 관계없이 HTTP API로 호출합니다. 관리형 클라우드 인스턴스와 자체 온프레미스 환경 모두에 같은 이미지를 배포할 수 있습니다.

**SDK**(C# 라이브러리로 기존 .NET 프로젝트에 직접 포함하는 경로)는 아직 없습니다 — 계획만 있는 상태입니다.

## 채널

**이메일**과 **앱 푸시** 두 채널이 있습니다.

- 이메일: SMTP로 직접 발송합니다([MailKit](https://github.com/jstedfast/MailKit) 사용). 발신 서버는 Gmail/Office365 프리셋이나 직접 지정한 호스트를 씁니다.
- 앱 푸시: Firebase Cloud Messaging으로 발송합니다([FirebaseAdmin](https://github.com/firebase/firebase-admin-dotnet) 사용). APNs(iOS 직접 연동)는 아직 없습니다.

새 채널은 공통 인터페이스를 구현하는 어댑터를 추가하는 방식으로 확장합니다.

## 테넌트/인증

`/messages/*` 호출은 `X-Api-Key` 헤더로 테넌트를 식별합니다 — 테넌트는 Sendway를 호출하는 서비스 하나에 대응합니다(1:1). 키가 없거나 비활성 테넌트의 키면 401을 반환합니다.

테넌트는 `/admin/*` 관리 API로 런타임에 등록합니다(`X-Admin-Key`로 보호되는 별도 관리자 키). 테넌트 생성 시 API 키가 평문으로 한 번만 반환되며, 이후에는 해시만 저장되어 재조회할 수 없습니다. `POST /admin/tenants/{id}/rotate-key`로 키를 교체할 수 있습니다.

## 통합 인터페이스

`POST /messages/email`, `POST /messages/push` 호출은 **동기적으로** 처리됩니다 — 발송이 끝날 때까지 기다렸다가 성공/실패를 그 자리에서 응답합니다. 성공(200)·실패(400/502) 모두 응답에 메시지 ID가 포함되며, `GET /messages/{id}`로 그 발송 건의 상태(채널·수신자·성공 여부·오류 메시지·발송 시각)를 나중에 다시 조회할 수 있습니다(pull 방식, 요청한 테넌트가 발신한 메시지만 조회 가능). 큐·재시도, 웹훅 등 push 방식의 상태 통지는 아직 없습니다.

## 저장

채널 자격증명과 발송 상태는 내장 SQLite 데이터베이스에 저장됩니다. 채널 자격증명은 **기본적으로 프로세스 시작 시 설정(환경변수 또는 설정 파일)으로 공급된 공용 값**을 쓰며, 테넌트별로 `PUT /admin/tenants/{id}/credentials/{channel}`을 통해 오버라이드를 등록할 수 있습니다(예: 테넌트마다 다른 Firebase 프로젝트). 두 경우 모두 [ASP.NET Core Data Protection API](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction)로 암호화한 뒤 기록합니다 — 특정 클라우드 벤더의 키 관리 서비스에 의존하지 않으므로 클라우드/온프레미스 배포 모두에서 동일하게 동작합니다. 암호화 키 자체는 별도 디렉터리(컨테이너 배포 시 `/data/dp-keys`, 볼륨 마운트 필요)에 파일로 보관됩니다. 메시지 템플릿 저장소는 아직 없습니다.
