# Token Bar for Windows

Codex와 Claude Code의 남은 사용량을 Windows 시스템 트레이에서 확인하는 초경량 앱입니다.

![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4)
![License](https://img.shields.io/badge/license-MIT-green)

![Token Bar 실행 화면](design/token-bar-app.png)

## 특징

- 단일 실행 파일: WebView, Electron, Python, 별도 서버가 필요 없습니다.
- Codex: 로컬 세션 로그의 `rate_limits` 이벤트를 읽습니다.
- Claude: 공식 `/usage` 화면을 숨겨진 Windows 가상 터미널로 최대 15분에 한 번 확인합니다.
- 인증정보를 읽거나 저장하지 않습니다. 각 CLI에 이미 로그인되어 있어야 합니다.
- 우측 하단 `CA` 트레이 아이콘을 누르면 시계 위에 Windows 11 스타일 패널이 열립니다.
- 패널 밖을 클릭하면 자동으로 닫히며 작업표시줄 위에 상시 오버레이하지 않습니다.
- Codex와 Claude의 5시간(`5h`) 및 주간(`7d`) 잔여량과 초기화까지 남은 시간을 표시합니다.

표시 값은 **남은 비율**입니다.

`C`는 Codex, `A`는 Claude입니다. 트레이 아이콘에 마우스를 올리면 요약값을, 클릭하면 전체 사용량 패널을 볼 수 있습니다.

## 설치

1. [Releases](https://github.com/tkjacob/token-bar-windows/releases/latest)에서 최신 ZIP을 내려받아 압축을 풉니다.
2. 압축을 푼 폴더에서 PowerShell을 열고 다음 명령을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

기본 설치 위치는 `%LOCALAPPDATA%\TokenBar`이며, 설치 직후 실행되고 Windows 로그인 시 자동으로 시작합니다.

삭제하려면 설치 폴더 또는 압축을 푼 폴더에서 다음 명령을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

> 로컬에서 빌드한 서명되지 않은 실행 파일을 Smart App Control이 차단하는 PC에서는 설치 패키지에 포함된 PowerShell 호스트가 같은 소스를 메모리에서 실행합니다. 보안 설정을 끌 필요가 없습니다.

## 소스에서 빌드

Windows PowerShell에서:

```powershell
.\build.ps1
.\run.ps1
```

기본 결과물은 `dist\TokenBar.exe` 하나입니다. Windows에 포함된 .NET Framework 컴파일러를 사용하므로 SDK 설치가 필요 없습니다.

`run.ps1`은 먼저 이 실행 파일을 시작합니다. Smart App Control이 로컬에서 빌드한 서명되지 않은 EXE를 차단하는 PC에서는, 보안 설정을 바꾸지 않고 Windows PowerShell 안에서 같은 소스를 메모리 컴파일해 자동 실행합니다.

- 일반 PC에 옮길 때: `dist\TokenBar.exe`만으로 실행 가능
- Smart App Control이 켜진 PC: 프로젝트 폴더를 유지하고 `run.ps1`로 실행

## 사용법

- `CA` 아이콘 클릭: Token Bar 패널 열기/닫기
- `CA` 아이콘 우클릭 → **지금 새로고침**: Codex 로그와 Claude `/usage` 즉시 갱신
- `CA` 아이콘 우클릭 → **Windows 시작 시 실행**: 사용자 시작프로그램 등록/해제
- 패널 우측 상단 새로고침 버튼: 즉시 갱신
- 패널 밖 클릭: 자동 닫기

Windows가 새 아이콘을 숨김 목록에 넣은 경우 작업표시줄의 숨겨진 아이콘 메뉴에서 `CA` 아이콘을 작업표시줄로 끌어오면 됩니다.

```ini
ClaudeRefreshMinutes=15
```

## 데이터의 의미와 한계

- Codex 로그는 Codex가 서버와 통신할 때 갱신됩니다. 마지막 기록의 초기화 시각이 이미 지났다면 해당 윈도우는 100% 남은 것으로 추정하고 툴팁에 `추정`으로 표시합니다.
- 여러 Codex 한도가 기록된 경우 기본 `codex` 한도를 우선합니다. 별도 모델 한도는 툴팁에 함께 표시합니다.
- Claude는 로컬에 구조화된 플랜 한도를 저장하지 않으므로 `/usage` 화면 캡처가 필요합니다. 캡처 중에도 콘솔 창은 뜨지 않습니다.
- 회사 정책이나 보안 제품이 가상 터미널 생성을 차단하면 Claude는 `--`로 표시되고 Codex만 계속 동작합니다.

## 개인정보

Token Bar가 저장·표시하는 정보는 사용률, 초기화 시각, 이벤트 시각뿐입니다. Claude `/usage` 화면 텍스트는 메모리에서 필요한 숫자만 해석한 뒤 버립니다. 프롬프트, 응답, API 키, OAuth 토큰은 수집하거나 캐시하지 않으며, Token Bar 자체가 별도 네트워크 요청을 보내지도 않습니다.

## 참고

macOS용 [Token Ghost](https://github.com/zoeymakes/token-ghost)의 가벼운 로컬 수집 방식을 참고해 Windows용으로 새로 구현했습니다. Token Ghost는 MIT 라이선스입니다.
