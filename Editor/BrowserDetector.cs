using System;
using System.IO;

namespace EditorBrowser
{
    internal enum BrowserKind
    {
        None,
        Chrome,
        Edge,
    }

    internal sealed class BrowserInfo
    {
        public BrowserKind Kind { get; }
        public string ExecutablePath { get; }

        public BrowserInfo(BrowserKind kind, string path)
        {
            Kind = kind;
            ExecutablePath = path;
        }

        public bool IsAvailable => Kind != BrowserKind.None && !string.IsNullOrEmpty(ExecutablePath);
    }

    /// <summary>
    /// Chrome/Edge 설치 감지. 레지스트리 의존을 피하고 알려진 설치 경로만 점검한다.
    /// (.NET Standard 2.1 환경에서 Microsoft.Win32.Registry는 별도 NuGet 패키지가 필요하므로
    ///  파일 시스템 기반 감지로 통일 — 휴대용 Chrome 등은 V2에서 EditorPref 오버라이드 지원 예정.)
    /// </summary>
    internal static class BrowserDetector
    {
        public static BrowserInfo Detect()
        {
            var chrome = FindFirstExisting(ChromeCandidatePaths());
            if (!string.IsNullOrEmpty(chrome))
                return new BrowserInfo(BrowserKind.Chrome, chrome);

            var edge = FindFirstExisting(EdgeCandidatePaths());
            if (!string.IsNullOrEmpty(edge))
                return new BrowserInfo(BrowserKind.Edge, edge);

            return new BrowserInfo(BrowserKind.None, null);
        }

        private static string[] ChromeCandidatePaths()
        {
            var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                Path.Combine(pf,  "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            };
        }

        private static string[] EdgeCandidatePaths()
        {
            var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[]
            {
                Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf,  "Microsoft", "Edge", "Application", "msedge.exe"),
            };
        }

        private static string FindFirstExisting(string[] paths)
        {
            foreach (var p in paths)
            {
                try
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return p;
                }
                catch
                {
                    // 권한·UNC 등 예외는 무시하고 다음 후보로
                }
            }
            return null;
        }
    }
}
