# Changelog

이 파일은 RepoRecon의 주요 변경 사항을 기록합니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 따릅니다.

## [1.0.0] - 2026-08-31

### Added

- 로컬 폴더 자동 스캔과 인덱싱 (수동 등록 없이 디스크의 Git 저장소를 탐지)
- upstream 기준 3축 상태 판정 (동기화 상태 / 작업 트리 상태 / 충돌 상태)
- 압축된 계층 트리 뷰, 폴더 단위 상태 롤업, 다축 조합 필터
- 백그라운드 점진적 fetch (호스트별 동시성 제어, 뷰포트 우선순위 재정렬)
- 일괄 Pull (`--ff-only`, dry-run 미리보기, 실패 격리)
- 서버 카탈로그 연동 (GitLab / GitHub / Gitea), 카탈로그 기반 선택적 클론
- 일괄 Push (체크아웃된 브랜치만 대상, 서버 측 dry-run 사전 검증)
- 작업 이력 조회, 안전한 되돌리기(Rollback), Remote URL 변경, 상태 내보내기(CSV/Markdown)
- 설치본(Inno Setup) 및 포터블 배포 형태 제공

### Fixed

- 폴더 노드 체크박스를 선택해도 하위 저장소 전체로 선택이 전파되지 않던 문제. `CascadeSelection` 프로퍼티가 XAML 바인딩에 연결되지 않고 `IsSelected`를 직접 바인딩하고 있었음.
- `BooleanToVisibilityConverter`가 `ConverterParameter`를 읽지 않아, 반전(`invert`) 로직을 사용하던 5곳(연결 확인 버튼, 일괄 치환 대상 판정, 클론 중복 경고, 동기화 중 버튼 비활성화, 클론 중 버튼 비활성화)에서 활성화·표시 상태가 항상 반대로 계산되고 있던 문제.
