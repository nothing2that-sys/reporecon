# RepoRecon

로컬 디스크에 분산된 다수의 Git 저장소를 자동으로 탐지하고, 원격 서버(GitLab, GitHub, Gitea) 프로파일과 대조하여 저장소들의 동기화 상태를 일괄 관리하는 Windows WPF 데스크톱 도구입니다.

---

## 🚫 이 도구가 하지 않는 것 (Non-Goals)

RepoRecon은 **다중 저장소의 상태 탐색·정합성 검증·일괄 동기화(Fetch/Pull/Push) 및 안전 관리**에 특화된 도구입니다. 아래 작업들은 설계(§1)상 의도적으로 범위 밖으로 정의되어 있으며, 기존의 전문 Git 클라이언트(Fork, SourceTree, GitKraken, VS Code, Git CLI 등)의 영역에 속합니다.

- **브랜치 생성·전환·머지**: 브랜치 체크아웃, 새 브랜치 분기, 브랜치 간 병합 작업을 수행하지 않습니다.
- **커밋 작성**: 작업 트리의 변경 사항을 스테이징(Add)하거나 커밋(Commit)을 생성하지 않습니다.
- **충돌 해결**: 파일 단위 충돌(3-way merge conflict) 해결 기능을 제공하지 않으며, 충돌 발생 시 안전하게 표시하고 작업을 중단합니다.
- **일괄 Force Push**: 일괄 푸시 화면에서 Force Push는 일체 지원하지 않으며, 개별 저장소 우클릭 메뉴에서만 `--force-with-lease`로 엄격한 확인을 거쳐 실행할 수 있습니다.

---

## 📚 문서 (Documentation)

모든 아키텍처와 구현 세부사항은 `docs/` 디렉터리에 체계적으로 문서화되어 있습니다.

- **[RepoRecon-design.md](docs/RepoRecon-design.md)**: 시스템 아키텍처, 3축 모델, SQLite 스키마, 안전 정책, UI 레이아웃을 정의하는 **단일 진실 원천(Single Source of Truth, SSOT)** 문서입니다.
- **[RepoRecon-impl-instructions.md](docs/RepoRecon-impl-instructions.md)**: 전체 개발 과정에 적용된 공통 구현 원칙 및 코딩 규칙입니다.
- **`docs/RepoRecon-phase*-instructions.md`**: Phase 1(기반 구조)부터 Phase 6(안전 장치 및 편의 기능)까지의 단계별 세부 명세입니다.

---

## 🏗️ 아키텍처 및 모듈 구성

```
RepoRecon/
├── src/
│   ├── RepoRecon.Core/        # 비즈니스 로직, 데이터 엔진, Git 서비스 (.NET 8)
│   │   ├── Data/              # SQLite 인덱스 저장소 및 스키마 마이그레이션
│   │   ├── Discovery/         # 로컬 저장소 탐지 엔진
│   │   ├── Tree/              # 3축 통합 트리 빌더 (Path/Host/Server/Status)
│   │   ├── Fetch/             # 병렬 네트워크 Fetch 엔진 (호스트 풀링 및 백오프)
│   │   ├── Pull/              # 일괄 Pull 엔진 (Fast-forward, 락 제어, 스킵 판정)
│   │   ├── Push/              # 일괄 Push 엔진 (Dry-run 검증, 충돌 가드)
│   │   ├── Server/            # GitLab/GitHub/Gitea REST API 클라이언트
│   │   ├── Security/          # Windows DPAPI 기반 토큰/암호 암호화 저장소
│   │   ├── Git/               # Git CLI 실행기, Remote URL 관리, 안전 롤백 서비스
│   │   └── Export/            # CSV/Markdown 상태 리포트 내보내기 (자격증명 마스킹)
│   │
│   └── RepoRecon.App/         # WPF MVVM 사용자 인터페이스 (.NET 8 Windows)
│       ├── ViewModels/        # CommunityToolkit.Mvvm 기반 ViewModel
│       └── Views/             # 계층 트리, 실행 패널, 확인 대화상자 XAML
│
├── tests/
│   ├── RepoRecon.Core.Tests/  # xUnit 단위 테스트 스위트 (200+ 테스트)
│   └── verify_phase*.ps1      # 단계별 정규 자동화 검증 스크립트
└── docs/                      # 설계 및 구현 지시서
```

---

## 🚀 빌드 및 실행 방법

### 사전 요구사항
- **.NET 8 SDK** (8.0.x 이상)
- **Git CLI** (2.30 이상, `PATH`에 등록 필요)
- **Windows 10 / 11**

### 1. 솔루션 빌드
```powershell
dotnet build
```

### 2. 애플리케이션 실행
```powershell
dotnet run --project src/RepoRecon.App
```

### 3. 단위 테스트 실행
```powershell
dotnet test
```

---

## 📦 설치 (배포본)

RepoRecon은 설치본(`.exe`)과 포터블(`.zip`) 두 형태로 배포됩니다. 두 산출물 모두 `build/build-release.ps1` 한 번 실행으로 `dist/`에 생성됩니다 (사전에 [Inno Setup](https://jrsoftware.org/isinfo.php)이 설치되어 있어야 합니다).

```powershell
pwsh -File build/build-release.ps1
```

### 설치본 (`RepoRecon-Setup-{version}.exe`)

1. `RepoRecon-Setup-{version}.exe`를 실행합니다.
3. 관리자 권한이 필요하지 않습니다 (`%LOCALAPPDATA%\Programs\RepoRecon`에 설치됩니다).
4. 데이터(`index.db`, `logs/`, `credentials/`)는 `%LOCALAPPDATA%\RepoRecon\`에 저장됩니다. 제거 시 이 데이터를 함께 삭제할지 물으며, 기본값은 "삭제 안 함"입니다.

### 포터블 (`RepoRecon-Portable-{version}.zip`)

포터블이 필요하면 zip을 받아 원하는 위치에 압축을 해제한 뒤 `RepoRecon.App.exe`를 실행하면 됩니다. 데이터는 exe 옆에 생성되며, 자세한 내용은 zip 안의 `README-portable.txt`를 참고하십시오.

---

## 🧪 Phase별 자동화 검증 스크립트

프로젝트 루트에서 PowerShell을 통해 각 단계별 검증 스크립트를 실행할 수 있습니다.

| 스크립트 | 검증 대상 및 내용 |
|---|---|
| `tests/verify_phase2_strict_criteria.ps1` | **Phase 2**: 로컬 저장소 디렉터리 스캔, SQLite 인덱싱, 3축 트리 롤업 |
| `tests/verify_phase3_strict_criteria.ps1` | **Phase 3**: 병렬 Fetch 네트워크 풀링, 로컬 상태 판정, 일괄 Fast-forward Pull 및 락 관리 |
| `tests/verify_phase4_strict_criteria.ps1` | **Phase 4**: 서버 프로파일(GitLab/GitHub/Gitea), DPAPI 자격증명, 카탈로그 동기화 |
| `tests/verify_phase5_real_server_and_ui_criteria.ps1` | **Phase 5**: 실서버(Gitea) E2E 연동, 일괄 Push 사전 검증, 카탈로그 원클릭 클론 |
| `tests/verify_phase6_strict_criteria.ps1` | **Phase 6**: 작업 이력(Job History), 안전 되돌리기(Rollback), Remote URL 관리, 상태 내보내기 |

실행 예시:
```powershell
pwsh -File tests/verify_phase6_strict_criteria.ps1
```
