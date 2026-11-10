# RepoRecon

RepoRecon은 로컬 Git 저장소의 상태를 한 화면에서 비교하고 GitHub·GitLab·Gitea 저장소 목록을 연결하는 Windows WPF 도구입니다.

- 검색 루트를 등록하고 브랜치, upstream, 변경 사항과 동기화 상태를 확인합니다.
- 선택한 저장소를 일괄 Fetch하거나 Pull하고 원격 저장소를 Clone합니다.
- 지정한 저장소를 예약 Pull 대상으로 설정할 수 있습니다.
- 원격 프로파일의 자격증명은 Windows DPAPI로 보호합니다. 상태 보고서는 CSV 또는 Markdown으로 내보낼 수 있습니다.

## 처음 사용하기

1. **로컬 관리**에서 **찾아보기…**로 여러 Git 저장소가 들어 있는 상위 폴더를 고른 뒤 **루트 추가**와 **스캔 시작**을 누릅니다.
2. 결과 목록에서 현재 브랜치, 추적 브랜치, 미커밋 변경, 원격과의 차이를 확인합니다. **새로고침 (F5)**으로 목록을 갱신할 수 있습니다.
3. 원격 변경을 가져올 저장소를 선택하고 **일괄 Pull 실행**에서 대상과 상태를 확인합니다. 상태를 공유하려면 **상태 내보내기**를 사용합니다.
4. 원격 저장소 목록도 관리하려면 프로파일 관리에서 GitHub·GitLab·Gitea 연결을 추가합니다. 로컬 저장소 스캔은 프로파일 없이 시작할 수 있습니다.

## 빌드와 실행

Windows, .NET 8 SDK, `PATH`에 등록된 Git CLI가 필요합니다.

```powershell
dotnet build RepoRecon.sln -c Release
dotnet test RepoRecon.sln -c Release
dotnet run --project src/RepoRecon.App
```

설치 파일과 포터블 ZIP을 만들려면 PowerShell 7과 Inno Setup을 준비한 뒤 `build/build-release.ps1`을 실행합니다. 산출물은 `dist/`에 생성됩니다.
