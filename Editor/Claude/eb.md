---
description: Drive the in-Unity-Editor browser via natural language (navigate / evaluate / screenshot through the editor_browser MCP tool).
argument-hint: natural-language browser request
---

You are about to drive the in-Editor browser hosted by the EditorBrowser
Unity package (`com.lwt.editor-browser`) for the user. The user's request
is below — interpret it and execute it via the `mcp__UnityMCP__editor_browser`
MCP tool. Reply in formal Korean (존댓말).

**User request**: $ARGUMENTS

---

## Execution recipe

### Step 1. Verify a target Unity instance is alive

Read `mcpforunity://instances`. You are looking for an instance whose
project has the `com.lwt.editor-browser` package installed (the package
that provides the `editor_browser` MCP tool).

**Selection rule** (no hardcoded project name):
- Run `pwd` (Bash) to get the current working directory.
- If exactly one instance is running, use it.
- If multiple are running, prefer the one whose `path` is the CWD itself
  or a subdirectory of CWD.
- If multiple still match (or none match CWD), ask the user **one**
  short question listing the candidates (id + last two path segments)
  and use their pick.

**If no instance is in the list** (auto-launch, Windows host assumed):
- Project path: use the CWD from `pwd` — the user is running Claude Code
  from the Unity project root they care about.
- Unity exe: list `C:/Program Files/Unity/Hub/Editor/` (and
  `D:/Program Files/Unity/Hub/Editor/` as fallback), pick the highest
  `6000.x` subdirectory whose `Editor/Unity.exe` exists.
- Launch (Bash, `run_in_background: true`):
  ```
  "<unity.exe>" -projectPath "<cwd>"
  ```
- Poll `mcpforunity://editor/state` until `advice.ready_for_tools` is true
  (usually 30–60 s on a cold start). Do **not** poll faster than every 5 s.
- If launch fails (no Unity Hub install at the standard paths, CWD is not
  a Unity project, or readiness never arrives) surface the failure with
  a one-line diagnosis — do not silently fall back.

### Step 2. Pin the active instance

Call `mcp__UnityMCP__set_active_instance` with the exact `<name>@<hash>`
id from the instances resource (e.g. `MyGame@a1b2c3d4`). Skipping this
risks the next MCP call landing in an unrelated open project on the host.

### Step 3. Open the BrowserWindow if it's not open

Call `mcp__UnityMCP__execute_menu_item` with `menu_path: Window/Editor Browser`.
Idempotent — if the window is already open this just refocuses it.

Briefly wait (1.5 s) so the host can spawn Chrome and attach the HWND.
If the user's request needs a non-default starting URL, you can skip
the wait and let step 4 handle the navigation.

### Step 4. Execute the user's request via `mcp__UnityMCP__editor_browser`

The tool accepts `{action, url?, expression?}`. Dispatch by intent:

- **"X를 열어줘 / open X / X로 가줘"** → `action=navigate, url=<resolved URL>`
- **"X에서 Y를 검색해줘"** → resolve to the site's search URL and navigate
  (e.g., Naver: `https://search.naver.com/search.naver?query=<URL-encoded Y>`,
  Google: `https://www.google.com/search?q=<URL-encoded Y>`).
- **"본문 내용 알려줘 / 페이지에 뭐 있어 / 제목 뭐야"** → `action=evaluate,
  expression=<JS>` returning the data (e.g., `document.title`,
  `document.querySelector('main')?.innerText`).
- **"클릭해줘 / 스크롤해줘"** → `action=evaluate` with a JS snippet that
  performs the DOM action; return a short confirmation string.
- **"화면 캡처해줘 / 스크린샷"** → `action=screenshot`. The response carries
  a base64 PNG under `base64` — decode and save under
  `%LOCALAPPDATA%\EditorBrowser\Screenshots\<timestamp>.png` if the user
  asks to keep it.

Chain calls when needed (e.g., navigate, wait briefly via a JS-based
"document.readyState" poll, then evaluate). Allow ~1–2 s after navigate
before evaluating to let the page paint.

### Step 5. URL resolution defaults

If the user names a Korean / general site without a URL, resolve it
via your general knowledge. Examples:
- 네이버 → `https://www.naver.com`
- 다음 → `https://www.daum.net`
- 구글 → `https://www.google.com`
- 유튜브 → `https://www.youtube.com`
- 위키 / 위키피디아 → `https://ko.wikipedia.org`

For "오늘자 / 최신" + topic, prefer the site's news / today section URL
when one exists; otherwise fall back to a site search.

### Step 6. Report to the user

Reply concisely with what was opened, what was found (if evaluate ran),
and any next step worth suggesting. Don't dump huge JSON — summarize.

---

## Constraints

- If the request is genuinely ambiguous (two equally plausible targets,
  required arg missing), ask **one** clarifying question before executing.
  If it's clear enough, just execute.
- If `mcp__UnityMCP__editor_browser` returns `ok: false`, surface the
  `error` string and propose a recovery (most common: close and reopen
  the Browser tab via Step 3).
- Do not auto-take screenshots — only on explicit ask.
- Do not navigate to sites that require login the user hasn't authorized.
  The browser uses an isolated profile (`%LOCALAPPDATA%\EditorBrowser\BrowserProfile`),
  so cookies don't leak from / to the user's regular Chrome.
- The MCP tool only registers when `com.coplaydev.unity-mcp` is installed
  in this project's `Packages/manifest.json`. If the tool is missing,
  tell the user to install UnityMCP, then re-run `/eb`.

## What to skip

- Do not start a PDCA cycle or any bkit agent for this. `/eb` is a
  direct execution skill, not a planning skill.
- Do not modify package source. If the request implies a code change
  (e.g., "add a new tool"), surface that and propose `/pdca plan` or
  similar instead.
