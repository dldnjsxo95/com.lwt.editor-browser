using System.Collections.Generic;

namespace EditorBrowser
{
    /// <summary>
    /// 단순 뒤로/앞으로 네비게이션 히스토리.
    /// 브라우저 표준 동작에 맞춰 forward 위치에서 새 Push 시 forward 히스토리는 잘라낸다.
    /// 현재는 UI 상태 관리용 — 이후 외부 브라우저 호스트와 동기화될 예정.
    /// </summary>
    internal sealed class BrowserHistory
    {
        private readonly List<string> _entries = new List<string>();
        private int _index = -1;

        /// <summary>현재 위치의 URL. 비어 있으면 null.</summary>
        public string Current => (_index >= 0 && _index < _entries.Count) ? _entries[_index] : null;

        public bool CanGoBack => _index > 0;

        public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

        public int Count => _entries.Count;

        /// <summary>
        /// 새 URL을 히스토리에 추가. 같은 URL 연속 push는 무시.
        /// 현재 위치가 끝이 아니면 그 뒤의 forward 히스토리를 잘라낸다.
        /// </summary>
        public void Push(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (Current == url) return;

            if (_index < _entries.Count - 1)
                _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

            _entries.Add(url);
            _index = _entries.Count - 1;
        }

        public string GoBack()
        {
            if (!CanGoBack) return Current;
            _index--;
            return Current;
        }

        public string GoForward()
        {
            if (!CanGoForward) return Current;
            _index++;
            return Current;
        }

        public void Clear()
        {
            _entries.Clear();
            _index = -1;
        }
    }
}
