# GyeolheePinball

결희 종겜핀볼은 SOOP 라이브 채팅에서 핀볼 추첨에 사용할 목록을 수집하고 관리하는 Windows Forms 프로그램입니다.

**바로 다운로드:** [결희 종겜핀볼(자동)](https://github.com/Gyeon-ai/GyeolheePinball/raw/refs/heads/main/%EA%B2%B0%ED%9D%AC%20%EC%A2%85%EA%B2%9C%ED%95%80%EB%B3%BC%28%EC%9E%90%EB%8F%99%29.exe) / [결희 종겜핀볼](https://github.com/Gyeon-ai/GyeolheePinball/raw/refs/heads/main/%EA%B2%B0%ED%9D%AC%20%EC%A2%85%EA%B2%9C%ED%95%80%EB%B3%BC.exe)

## Versions

| Project | Release file | Description |
|---|---|---|
| `GyeolheePinball` | `결희 종겜핀볼.exe` | 결희 UI를 사용하며 핀볼 사이트를 일반 방식으로 엽니다. |
| `GyeolheePinballAuto` | `결희 종겜핀볼(자동).exe` | 목록이 있으면 Chrome 우선으로 핀볼 사이트를 열고 입력칸 자동 반영을 시도합니다. |

## Features

- SOOP 라이브 채팅 연결
- 닉네임 모드에서는 기준을 충족한 후원을 즉시 수집
- 채팅 내용 모드에서는 기준을 충족한 후원자의 다음 채팅 1회 수집
- 도전미션 후원 패킷(`CHALLENGE_GIFT`) 수집 지원
- 애드벌룬 후원 패킷(`serviceCommand == 87`) 수집 지원
- 별풍선, 애드벌룬, 도전미션 수집 대상 선택
- 정확히 N개 또는 N개 이상 수집 조건 선택
- 닉네임 또는 채팅 내용 기준 핀볼 목록 생성
- 수집 목록 검색, 복사, 저장
- [결희 종겜핀볼](https://gyeon-ai.github.io/GyeolheePinball-Web/) 사이트 열기
- 자동 버전의 Chrome 우선 핀볼 사이트 입력 반영
- 결희 라벤더·스카이 블루 UI와 고해상도 아이콘

## Build Requirements

- Windows
- Visual Studio 2022 또는 Visual Studio Build Tools
- .NET Framework 4.8 Targeting Pack
- MSBuild

## Build

Visual Studio에서 `GyeolheePinball.sln`을 열고 `Release` 구성으로 빌드할 수 있습니다.

명령줄에서는 다음처럼 번호가 붙은 Release 빌드를 만들 수 있습니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Build-Release.ps1" -Project All -Configuration Release
```

특정 버전만 빌드하려면 `-Project GyeolheePinball` 또는 `-Project GyeolheePinballAuto`를 사용합니다.

빌드 결과는 아래 경로에 생성됩니다.

```text
dist\Release\GyeolheePinball\GyeolheePinball-001.exe
dist\Release\GyeolheePinballAuto\GyeolheePinballAuto-001.exe
```

## Repository Layout

```text
GyeolheePinball/
GyeolheePinballAuto/
tools/
GyeolheePinball.sln
```

## Release Files

| File | Version | SHA256 |
|---|---|---|
| `결희 종겜핀볼.exe` | `1.0.0.0` | `A66CDBE3D6414FE18C98098EB0B45D4A3CC38F247E7ABEDB78ED21AFD8D32CE6` |
| `결희 종겜핀볼(자동).exe` | `1.0.0.0` | `9FCD60B06F550123F3CCC9DF16C450C183DCC1BD3E2E02B31648FDF5A9239920` |

## Version Info

- Company: Gyeona
- Version: 1.0.0.0
- Target framework: .NET Framework 4.8

## Security Note

일반판에는 브라우저 원격 제어 코드가 포함되지 않습니다. `GyeolheePinballAuto`만 Chrome DevTools WebSocket을 사용해 핀볼 사이트 입력칸에 목록을 자동 반영하며, 이 동작은 일부 보안 엔진이나 ML 판정에서 민감하게 보일 수 있습니다.
