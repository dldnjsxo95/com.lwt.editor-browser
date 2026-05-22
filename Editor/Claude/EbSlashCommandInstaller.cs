using System;
using System.IO;
using UnityEditor;
using UnityEngine;
// Disambiguate from UnityEditor.PackageInfo (legacy AssetStore type).
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace EditorBrowser
{
    /// <summary>
    /// One-click installer for the bundled <c>eb.md</c> Claude Code slash
    /// command. The package ships its canonical copy at
    /// <c>Editor/Claude/eb.md</c>; this menu copies it to either the
    /// user-global Claude commands folder
    /// (<c>%USERPROFILE%\.claude\commands\</c> on Windows /
    /// <c>~/.claude/commands/</c> elsewhere) or the current Unity project's
    /// <c>.claude/commands/</c> folder, removing the manual copy step the
    /// README previously required.
    ///
    /// <para>Lives in <c>EditorBrowser.Editor</c> (the main asmdef, no
    /// MCP dependency). The /eb command itself needs
    /// <c>com.coplaydev.unity-mcp</c> at runtime to actually drive the
    /// browser — the menu still installs the file regardless, and eb.md
    /// self-documents that requirement.</para>
    /// </summary>
    internal static class EbSlashCommandInstaller
    {
        private const string MenuPath = "Window/Editor Browser Setup/Install eb Slash Command...";
        private const int MenuPriority = 2020;

        [MenuItem(MenuPath, priority = MenuPriority)]
        public static void Install()
        {
            try
            {
                // EditorUtility.DisplayDialogComplex returns:
                //   0 = "ok" button     → user-global
                //   1 = "cancel" button → no-op
                //   2 = "alt" button    → project-local
                var choice = EditorUtility.DisplayDialogComplex(
                    "EditorBrowser — Install eb Slash Command",
                    "Install the /eb Claude Code slash command. Pick a scope:\n\n" +
                    "• User-Global — copies into %USERPROFILE%\\.claude\\commands\\eb.md " +
                    "so /eb is available in every Claude Code session.\n\n" +
                    "• Current Project — copies into this Unity project's " +
                    ".claude/commands/eb.md (only this project).",
                    "User-Global",
                    "Cancel",
                    "Current Project");

                switch (choice)
                {
                    case 0:
                        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        InstallTo(home, "user-global");
                        break;
                    case 2:
                        // Application.dataPath is "<project>/Assets" — walk one level up.
                        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        InstallTo(project, "current-project");
                        break;
                    // case 1 (cancel) deliberately no-op
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "EditorBrowser — Install eb Slash Command",
                    "Install failed: " + ex.Message,
                    "OK");
            }
        }

        private static void InstallTo(string rootDir, string label)
        {
            if (string.IsNullOrEmpty(rootDir))
            {
                EditorUtility.DisplayDialog(
                    "EditorBrowser — Install eb Slash Command",
                    "Could not resolve the " + label + " root directory. " +
                    "USERPROFILE / HOME may be unset.",
                    "OK");
                return;
            }

            var src = ResolvePackagedEbMd();
            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                EditorUtility.DisplayDialog(
                    "EditorBrowser — Install eb Slash Command",
                    "Could not find the bundled eb.md inside the package. " +
                    "The package install may be corrupted (expected at " +
                    "Editor/Claude/eb.md inside com.lwt.editor-browser).",
                    "OK");
                return;
            }

            var destDir = Path.Combine(rootDir, ".claude", "commands");
            var destPath = Path.Combine(destDir, "eb.md");

            if (File.Exists(destPath))
            {
                var ok = EditorUtility.DisplayDialog(
                    "EditorBrowser — Install eb Slash Command",
                    "An eb.md already exists at:\n\n" + destPath +
                    "\n\nOverwrite with the bundled v" + GetPackageVersion() + " copy?",
                    "Overwrite",
                    "Cancel");
                if (!ok) return;
            }

            Directory.CreateDirectory(destDir);
            File.Copy(src, destPath, overwrite: true);

            EditorUtility.DisplayDialog(
                "EditorBrowser — Install eb Slash Command",
                "Installed the " + label + " /eb slash command to:\n\n" + destPath +
                "\n\nStart a new Claude Code session (or restart Claude Code) to " +
                "pick up the new /eb command. Note: /eb needs com.coplaydev.unity-mcp " +
                "installed in the target Unity project to actually drive the browser.",
                "OK");
        }

        private static string ResolvePackagedEbMd()
        {
            var pkg = PackageInfo.FindForAssembly(typeof(EbSlashCommandInstaller).Assembly);
            if (pkg == null || string.IsNullOrEmpty(pkg.resolvedPath)) return null;
            return Path.Combine(pkg.resolvedPath, "Editor", "Claude", "eb.md");
        }

        private static string GetPackageVersion()
        {
            var pkg = PackageInfo.FindForAssembly(typeof(EbSlashCommandInstaller).Assembly);
            return pkg != null && !string.IsNullOrEmpty(pkg.version) ? pkg.version : "unknown";
        }
    }
}
