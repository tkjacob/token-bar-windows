# Token Bar for Windows

Codex와 Claude Code의 남은 사용량을 Windows 시스템 트레이에서 확인하는 초경량 앱입니다.

![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4)
![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.0.3-blue)

![Token Bar 실행 화면](design/token-bar-app.png)

## 특징

- 단일 실행 파일: WebView, Electron, Python, 별도 서버가 필요 없습니다.
- Codex: 로컬 세션 로그의 `rate_limits` 이벤트를 읽습니다.
- Claude: 공식 `/usage` 화면을 숨겨진 Windows 가상 터미널로 최대 15분에 한 번 확인합니다.
- Codex 로그 검색과 파싱은 백그라운드에서 수행되므로 로그가 많이 쌓여도 트레이 클릭을 막지 않습니다.
- Claude CLI는 PATH의 native 실행 파일, npm의 `.cmd`·PowerShell wrapper 및 확장자 없는 실행 shim을 지원합니다.
- 인증정보를 읽거나 저장하지 않습니다. 각 CLI에 이미 로그인되어 있어야 합니다.
- 하루에 최대 한 번 공개 GitHub Release를 확인하고 새 버전이 있으면 트레이 점과 앱 알림으로 안내합니다.
- 우측 하단 `CA` 트레이 아이콘을 누르면 시계 위에 Windows 11 스타일 패널이 열립니다.
- 패널 밖을 클릭하면 자동으로 닫히며 작업표시줄 위에 상시 오버레이하지 않습니다.
- Codex는 주간(`7d`) 잔여량을, Claude는 5시간(`5h`) 및 주간(`7d`) 잔여량을 표시합니다.

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

파일 탐색기에서 실행하려면 `run.cmd`를 더블클릭합니다. Windows에서 `.ps1` 파일이
메모장으로 연결되어 있어도 `run.cmd`가 PowerShell을 통해 Token Bar를 실행합니다.

기본 결과물은 `dist\TokenBar.exe` 하나입니다. Windows에 포함된 .NET Framework 컴파일러를 사용하므로 SDK 설치가 필요 없습니다.

`run.ps1`은 먼저 이 실행 파일을 시작합니다. Smart App Control이 로컬에서 빌드한 서명되지 않은 EXE를 차단하는 PC에서는, 보안 설정을 바꾸지 않고 Windows PowerShell 안에서 같은 소스를 메모리 컴파일해 자동 실행합니다.

- 일반 PC에 옮길 때: `dist\TokenBar.exe`만으로 실행 가능
- Smart App Control이 켜진 PC: 프로젝트 폴더를 유지하고 `run.cmd`를 더블클릭하거나 `run.ps1`로 실행

회귀 테스트를 실행하려면:

```powershell
.\tests\run-tests.ps1
```

테스트용 파일과 컴파일 결과는 프로젝트의 `.codex-tmp` 안에서만 생성되며 테스트 종료 시 제거됩니다.

## 사용법

- `CA` 아이콘 클릭: Token Bar 패널 열기/닫기
- `CA` 아이콘 우클릭 → **지금 새로고침**: Codex 로그와 Claude `/usage` 즉시 갱신
- `CA` 아이콘 우클릭 → **Windows 시작 시 실행**: 사용자 시작프로그램 등록/해제
- 패널 우측 상단 새로고침 버튼: 즉시 갱신
- 트레이의 업데이트 점 또는 패널의 `vX.Y.Z 업데이트` 클릭: 최신 GitHub Release 페이지 열기
- 패널 밖 클릭: 자동 닫기

Windows가 새 아이콘을 숨김 목록에 넣은 경우 작업표시줄의 숨겨진 아이콘 메뉴에서 `CA` 아이콘을 작업표시줄로 끌어오면 됩니다.

```ini
ClaudeRefreshMinutes=15
ShowCodexFiveHour=false
```

Codex의 5시간 제한이 다시 표시되어야 하는 경우 `ShowCodexFiveHour=true`로
변경하면 기존 5시간 수집·계산 코드를 그대로 사용해 행과 툴팁을 복원할 수 있습니다.

## 데이터의 의미와 한계

- Codex 로그는 Codex가 서버와 통신할 때 갱신됩니다. 마지막 기록의 초기화 시각이 이미 지났다면 해당 윈도우는 100% 남은 것으로 추정하고 툴팁에 `추정`으로 표시합니다.
- [Plus·Business·Pro 플랜의 Codex 5시간 제한이 일시적으로 해제된 정책](https://x.com/thsottiaux/status/2076365965915467978)에 맞춰 기본 화면에서는 Codex `5h`를 숨깁니다. 관련 bucket 파싱과 계산 코드는 향후 복원에 대비해 유지합니다.
- 여러 Codex 한도가 기록된 경우 기본 `codex` 한도를 우선합니다. 별도 모델 한도는 툴팁에 함께 표시합니다.
- Claude는 로컬에 구조화된 플랜 한도를 저장하지 않으므로 `/usage` 화면 캡처가 필요합니다. 캡처 중에도 콘솔 창은 뜨지 않습니다.
- 회사 정책이나 보안 제품이 가상 터미널 생성을 차단하면 Claude는 `--`로 표시되고 Codex만 계속 동작합니다.

## 개인정보

Token Bar가 저장·표시하는 정보는 사용률, 초기화 시각, 이벤트 시각뿐입니다. Claude `/usage` 화면 텍스트는 메모리에서 필요한 숫자만 해석한 뒤 버립니다. 프롬프트, 응답, API 키, OAuth 토큰은 수집하거나 캐시하지 않습니다.

업데이트 확인 시 하루에 최대 한 번
`api.github.com/repos/tkjacob/token-bar-windows/releases/latest`의 공개 Release
메타데이터만 인증 없이 조회합니다. Codex·Claude 사용량이나 사용자 식별정보는 전송하지
않으며, 사용자의 클릭 없이 파일을 다운로드하거나 설치하지 않습니다.

## v1.0.3

- Codex 5시간 제한의 일시 해제를 반영해 기본 화면과 툴팁에서 Codex `5h`만 숨겼습니다.
- `ShowCodexFiveHour=true` 설정으로 기존 Codex 5시간 표시를 즉시 복원할 수 있습니다.
- UI를 막지 않는 공개 GitHub Release 확인과 24시간 요청 제한을 추가했습니다.
- 새 버전이 있으면 트레이 아이콘의 점, 패널 배지, 버전당 한 번의 앱 알림으로 안내합니다.
- 업데이트 배지나 알림을 클릭하면 검증된 Token Bar GitHub Release 페이지만 엽니다.

## v1.0.2

- `.ps1` 파일이 메모장으로 연결된 PC에서도 파일 탐색기에서 바로 실행할 수 있도록 `run.cmd`를 추가했습니다.
- 설치본과 배포 ZIP에 `run.cmd`가 포함됩니다.

## v1.0.1

- 대규모 Codex 세션 폴더에서도 최신 80개 후보만 유지하고 수집을 UI 스레드 밖에서 수행합니다.
- Claude 수집의 입력 대기 시간을 전체 15초 제한에 포함했습니다.
- `claude.exe`, `.com`, `.cmd`, `.bat`, `.ps1`, 확장자 없는 native shim을 PATH와 PATHEXT 기준으로 탐색합니다.
- 제거 시 설치 폴더 경계를 확인해 `TokenBarBackup` 같은 형제 폴더의 프로세스를 종료하지 않습니다.

## 참고

macOS용 [Token Ghost](https://github.com/zoeymakes/token-ghost)의 가벼운 로컬 수집 방식을 참고해 Windows용으로 새로 구현했습니다. Token Ghost는 MIT 라이선스입니다.
