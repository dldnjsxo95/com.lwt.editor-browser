using System;
using System.Linq;

namespace EditorBrowser
{
    /// <summary>
    /// 사용자 입력을 (1) 유효 URL이면 그대로, (2) 도메인 형태면 https:// 접두 부착,
    /// (3) 그 외에는 Google 검색 쿼리로 변환한다.
    /// 순수 함수 — Unity 의존 없음, 단위 테스트 용이.
    /// </summary>
    internal static class UrlResolver
    {
        internal const string DefaultHomepage = "https://www.google.com/";
        private const string GoogleSearchPrefix = "https://www.google.com/search?q=";

        public static string Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return DefaultHomepage;

            var trimmed = input.Trim();

            // 1) 명시적 http/https scheme → 그대로 사용
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return trimmed;
            }

            // 2) 호스트처럼 보이면 https:// 접두 부착
            if (LooksLikeHost(trimmed))
                return "https://" + trimmed;

            // 3) 검색 쿼리
            return GoogleSearchPrefix + Uri.EscapeDataString(trimmed);
        }

        private static bool LooksLikeHost(string s)
        {
            // 공백 포함하면 호스트 아님 (검색어로 처리)
            if (s.IndexOf(' ') >= 0) return false;

            // localhost / localhost:port
            if (s == "localhost" || s.StartsWith("localhost:", StringComparison.Ordinal)) return true;

            // path/query 떼어내고 호스트 부분만
            var hostPart = s;
            var slashIdx = hostPart.IndexOf('/');
            if (slashIdx >= 0) hostPart = hostPart.Substring(0, slashIdx);

            // 점이 없으면 도메인 아님
            var lastDot = hostPart.LastIndexOf('.');
            if (lastDot < 0 || lastDot == hostPart.Length - 1) return false;

            // 마지막 dot 이후가 TLD 후보 (port 분리)
            var tldPart = hostPart.Substring(lastDot + 1);
            var colonIdx = tldPart.IndexOf(':');
            if (colonIdx >= 0) tldPart = tldPart.Substring(0, colonIdx);

            // TLD는 2자 이상, 알파벳만 (IPv4는 의도적으로 검색으로 흘려보내지 않게 별도 처리 가능하나
            // 현재는 단순 휴리스틱으로 충분 — 사용자가 명시적 http:// 붙여도 1)에서 잡힘)
            return tldPart.Length >= 2 && tldPart.All(char.IsLetter);
        }
    }
}
