# RepoRecon

RepoRecon은 로컬 Git 저장소의 상태를 한 화면에서 비교하고 GitHub·GitLab·Gitea 저장소 목록을 연결하는 Windows WPF 도구입니다.

- 검색 루트를 등록하고 브랜치, upstream, 변경 사항과 동기화 상태를 확인합니다.
- 선택한 저장소를 일괄 Fetch하거나 Pull하고 원격 저장소를 Clone합니다.
- 지정한 저장소를 예약 Pull 대상으로 설정할 수 있습니다.
- 원격 프로파일의 자격증명은 Windows DPAPI로 보호합니다. 상태 보고서는 CSV 또는 Markdown으로 내보낼 수 있습니다.

## 빌드와 실행

Windows, .NET 8 SDK, `PATH`에 등록된 Git CLI가 필요합니다.

```powershell
dotnet build RepoRecon.sln -c Release
dotnet test RepoRecon.sln -c Release
dotnet run --project src/RepoRecon.App
```

설치 파일과 포터블 ZIP을 만들려면 PowerShell 7과 Inno Setup을 준비한 뒤 `build/build-release.ps1`을 실행합니다. 산출물은 `dist/`에 생성됩니다.

