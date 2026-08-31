# RepoRecon 패키징 지시서 — 설치본 + 포터블

> 저장소: `<repository-root>`
> 선행: `docs/RepoRecon-design.md` **§13 배포**, §9.3 데이터 저장 위치
> 전제: Inno Setup이 이 PC에 설치되어 있다
> 정지: §6 완료 기준을 충족하면 멈추고 승인을 요청한다. 커밋하지 않는다.

---

## 0. 산출물

한 번의 빌드로 두 개가 나와야 한다.

```
dist/
  RepoRecon-Setup-{version}.exe        설치본
  RepoRecon-Portable-{version}.zip     포터블
```

`dist/`는 `.gitignore`에 추가한다. 빌드 산출물을 커밋하지 않는다.

---

## 1. 버전 단일 출처

**버전을 두 곳에 적으면 반드시 어긋난다.**

`src/RepoRecon.App/RepoRecon.App.csproj`의 `<Version>`을 유일한 출처로 삼는다. 없으면 추가한다(시작값 `1.0.0`).

빌드 스크립트가 이 값을 읽어 Inno에 `/D` 파라미터로 넘긴다. `.iss` 파일에 버전을 하드코딩하지 않는다.

```powershell
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version
```

`AssemblyInformationalVersion`도 같은 값을 쓰게 해서 앱 안에서 버전을 표시할 수 있게 한다.

---

## 2. 빌드 스크립트

`build/build-release.ps1` 하나로 전 과정이 끝나야 한다.

### 순서

1. `dist/` 정리
2. `dotnet publish` — self-contained 단일 파일
3. 포터블 zip 구성 — publish 결과 + `reporecon.portable` 마커
4. Inno Setup 컴파일 — `iscc.exe` 호출
5. 결과 요약 출력 (파일명, 크기, 버전)

### publish 설정

```
-c Release -r win-x64 --self-contained true
-p:PublishSingleFile=true
```

`PublishReadyToRun`은 **켜지 않는다.** 시작 시간이 조금 줄지만 파일 크기가 더 늘고, 이미 150MB다.

### iscc 경로

레지스트리나 기본 설치 경로에서 찾되, 못 찾으면 **명확한 오류 메시지와 함께 중단**한다. 조용히 건너뛰면 설치본 없이 zip만 나온 걸 모르고 넘어간다.

---

## 3. Inno Setup 스크립트

`build/RepoRecon.iss`

### 필수 설정

| 항목 | 값 | 이유 |
|---|---|---|
| `AppId` | **고정 GUID** | 재설치를 업그레이드로 처리. 바꾸면 중복 등록됨 |
| `PrivilegesRequired` | `lowest` | 관리자 권한 요구하지 않음 (설계 §13.2) |
| `DefaultDirName` | `{autopf}\RepoRecon` | `lowest`에서는 `%LOCALAPPDATA%\Programs`로 해석됨 |
| `ArchitecturesAllowed` | `x64compatible` | win-x64 빌드 |
| `Compression` | `lzma2/max` | 150MB를 줄이는 데 필요 |
| `SolidCompression` | `yes` | |
| `CloseApplications` | `yes` | 실행 중일 때 업데이트 설치 처리 |
| `UninstallDisplayIcon` | `{app}\RepoRecon.App.exe` | 제어판 목록에 아이콘 표시 |
| `OutputBaseFilename` | `RepoRecon-Setup-{#AppVersion}` | |
| `DisableProgramGroupPage` | `yes` | 단일 앱이라 그룹 선택이 불필요 |

`AppId` GUID는 한 번 생성해 `.iss`에 박고 **다시는 바꾸지 않는다.** 주석으로 그 사실을 적어둔다.

### 바로가기

- 시작 메뉴: 항상
- 바탕화면: `[Tasks]`로 선택 제공, **기본 선택**

### 제거 시 데이터 처리 (설계 §13.3)

`[Code]` 섹션에서 처리한다.

- 제거 시 `%LOCALAPPDATA%\RepoRecon\` 존재 여부를 확인
- 있으면 **"설정, 인덱스, 저장된 토큰도 함께 삭제하시겠습니까?"**를 묻는다
- **기본은 삭제하지 않음.** 재설치 시 스캔 루트와 프로파일이 유지되는 편이 낫다
- 사용자가 동의하면 `index.db`, `logs/`, `credentials/`를 지운다

**이 선택지를 넣지 않으면 안 된다.** 토큰이 남는데 사용자가 모르는 상태가 되거나, 반대로 설정이 통째로 날아간다.

---

## 4. 포터블 zip

publish 결과에 **`reporecon.portable` 빈 파일을 추가**해서 압축한다. 이 마커가 있으면 앱이 exe 옆에 데이터를 만든다(설계 §9.3).

zip 안에 `README-portable.txt`를 넣고 아래를 적는다.

- 압축을 풀고 `RepoRecon.App.exe`를 실행하면 된다
- 데이터가 exe 옆 `index.db`, `logs/`, `credentials/`에 생긴다
- **저장된 토큰은 Windows 사용자 계정으로 암호화되므로 다른 PC로 옮기면 재입력이 필요하다** (설계 §9.3)
- `reporecon.portable` 파일을 지우면 일반 모드로 동작한다

---

## 5. 설치 안내 문구

**서명하지 않으므로 첫 실행 시 SmartScreen 경고가 뜬다**(설계 §13.4). 안내가 없으면 사용자가 악성코드로 오인한다.

`README.md`에 "설치" 절을 추가하고 아래를 적는다.

- `RepoRecon-Setup-{version}.exe` 실행
- **"Windows의 PC 보호" 경고가 뜨면 "추가 정보" → "실행"**
- 사내 배포용이라 코드 서명을 하지 않았고, 정상 동작이다
- 포터블이 필요하면 zip을 받아 압축 해제

---

## 6. 완료 기준

| # | 기준 | 검증 |
|---|---|---|
| 1 | `build-release.ps1` 한 번 실행으로 두 산출물이 나온다 | 실제 실행 |
| 2 | 버전이 csproj 한 곳에서만 온다 | `.iss`에 하드코딩 버전 없음 |
| 3 | 산출물 파일명에 버전이 들어간다 | 파일명 확인 |
| 4 | **설치본이 관리자 권한 없이 설치된다** | 실제 설치. UAC 프롬프트 없음 |
| 5 | 설치 후 시작 메뉴·바탕화면 바로가기가 동작한다 | 실제 클릭 |
| 6 | **설치된 앱이 실행되고 스캔이 동작한다** | 실제 실행 |
| 7 | 설치본이 `%LOCALAPPDATA%\RepoRecon\`에 데이터를 만든다 | 폴더 확인 |
| 8 | **같은 AppId로 재설치 시 업그레이드로 처리된다** | 두 번 설치 후 제어판에 항목 1개 |
| 9 | 제거 시 데이터 삭제 여부를 묻는다 | 실제 제거 |
| 10 | **기본값(삭제 안 함)일 때 데이터가 남는다** | 제거 후 폴더 확인 |
| 11 | 동의했을 때 데이터가 지워진다 | 제거 후 폴더 확인 |
| 12 | 제거 후 설치 폴더가 남지 않는다 | 폴더 확인 |
| 13 | **포터블 zip이 exe 옆에 데이터를 만든다** | 압축 해제 후 실행. `%LOCALAPPDATA%` 오염 없음 확인 |
| 14 | 포터블에 `README-portable.txt`가 있다 | zip 내용 확인 |
| 15 | 아이콘이 설치본·바로가기·제어판에 적용된다 | 각각 확인 |
| 16 | `dist/`가 `.gitignore`에 있다 | `git status` |
| 17 | iscc를 못 찾으면 명확한 오류로 중단한다 | 경로를 임시로 틀리게 해서 확인 |
| 18 | 산출물 크기를 보고한다 | 수치 |

**4·8·10·13이 가장 중요하다.** 권한 요구, 업그레이드 처리, 데이터 보존, 포터블 격리다.

---

## 7. 보고 요구

1. 완료 기준 18개 각각. 실제 설치·제거·재설치를 수행한 결과
2. 산출물 파일명과 크기
3. 설치 화면 캡처 (권한 프롬프트가 없는 것이 보이게)
4. 제거 시 데이터 삭제 확인 대화상자 캡처
5. **지시서·설계에 없어 임의로 판단한 항목 전부** — GUID 값, 압축 옵션, 폴더 구조, 문구 등
6. 변경·생성 파일 목록

실행하지 않은 검증을 했다고 쓰지 않는다. 실제 설치 없이 스크립트만 작성한 것은 미검증이다.

---

## 8. 하지 않을 것

- 코드 서명 (설계 §13.4)
- 자동 업데이트 기능
- `--no-self-contained` 전환 (설계 §13.5)
- 빌드 산출물 커밋
- 승인 전 커밋

문서에 없는 결정을 마주치면 임의로 정하지 말고 멈추고 묻는다.
