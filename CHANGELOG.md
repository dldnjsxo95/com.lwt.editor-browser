# Changelog

본 패키지는 [Keep a Changelog](https://keepachangelog.com/) 규약과 [SemVer](https://semver.org/)를 따른다.

## [Unreleased]

### Added
- `BrowserWindow` EditorWindow — 툴바(뒤로/앞으로/새로고침/URL), 본문 placeholder, 상태바
- `UrlResolver` — Enter 입력을 URL/도메인/검색 쿼리로 분기하는 순수 함수
- `BrowserHistory` — 뒤로/앞으로 네비게이션 상태 관리
- 메뉴 항목 `Window > Editor Browser` + 단축키 Shift+Alt+W
- 기본 홈페이지 `https://www.google.com/`

### Notes
- 실제 브라우저 임베드(`ExternalBrowserHost`)는 후속 작업. 현재는 UI 쉘만.
