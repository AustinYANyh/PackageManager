using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public sealed class AiCommitSkillService
    {
        public const string SkillDirectoryName = "git_svn_commitlog_generator";
        private static readonly object SkillSyncGate = new object();

        private static readonly IDictionary<string, string> EmbeddedSkillFiles = new Dictionary<string, string>
        {
            { "PackageManager.AiCommitSkill.SKILL.md", "SKILL.md" },
            { "PackageManager.AiCommitSkill.scripts.get-working-changes.ps1", Path.Combine("scripts", "get-working-changes.ps1") },
            { "PackageManager.AiCommitSkill.scripts.invoke-commit-push-interactive.ps1", Path.Combine("scripts", "invoke-commit-push-interactive.ps1") },
            { "PackageManager.AiCommitSkill.scripts.invoke-working-changes-interactive.ps1", Path.Combine("scripts", "invoke-working-changes-interactive.ps1") },
            { "PackageManager.AiCommitSkill.scripts.run-commit-push-choice.ps1", Path.Combine("scripts", "run-commit-push-choice.ps1") },
            { "PackageManager.AiCommitSkill.scripts.terminal-host.ps1", Path.Combine("scripts", "terminal-host.ps1") },
        };

        public AiCommitSkillInfo EnsureSkillAvailable(string repositoryPath)
        {
            lock (SkillSyncGate)
            {
                var sourcePath = ExtractEmbeddedSkill();
                var skillMarkdownPath = Path.Combine(sourcePath, "SKILL.md");
                var wrapperPath = Path.Combine(sourcePath, "scripts", "invoke-working-changes-interactive.ps1");
                if (!File.Exists(skillMarkdownPath))
                {
                    throw new FileNotFoundException($"找不到内嵌提交 skill 说明文件：{skillMarkdownPath}");
                }

                if (!File.Exists(wrapperPath))
                {
                    throw new FileNotFoundException($"找不到内嵌提交采集脚本：{wrapperPath}");
                }

                var userSkillTargets = GetUserSkillTargets().ToList();
                var syncedUserSkillPaths = new List<string>();
                foreach (var target in userSkillTargets)
                {
                    SyncSkillDirectory(sourcePath, target);
                    syncedUserSkillPaths.Add(target);
                }

                var repositorySkillPath = GetRepositorySkillTarget(repositoryPath);
                var detectedRepositorySkillPath = !string.IsNullOrWhiteSpace(repositorySkillPath) && Directory.Exists(repositorySkillPath)
                    ? repositorySkillPath
                    : null;

                return new AiCommitSkillInfo(sourcePath, sourcePath, skillMarkdownPath, wrapperPath, syncedUserSkillPaths, detectedRepositorySkillPath);
            }
        }

        public AiCommitRunStateInfo CreateRunState(string repositoryPath, string engineName)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                throw new DirectoryNotFoundException("请选择有效的代码仓库。");
            }

            var safeEngineName = ToSafeFileNamePart(engineName, "ai");
            var stateDirectoryName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safeEngineName}-{Guid.NewGuid():N}";
            var stateDirectoryPath = Path.Combine(repositoryPath, ".pm-ai", "commit-state", stateDirectoryName);
            Directory.CreateDirectory(stateDirectoryPath);

            return new AiCommitRunStateInfo(
                stateDirectoryPath,
                Path.Combine(stateDirectoryPath, "last_changes.json"),
                Path.Combine(stateDirectoryPath, "last_changes_model.json"));
        }

        private static string ExtractEmbeddedSkill()
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PackageManager",
                "Skills",
                SkillDirectoryName);
            Directory.CreateDirectory(cacheRoot);

            var assembly = Assembly.GetExecutingAssembly();
            foreach (var resource in EmbeddedSkillFiles)
            {
                var destinationPath = Path.Combine(cacheRoot, resource.Value);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                using (var source = assembly.GetManifestResourceStream(resource.Key))
                {
                    if (source == null)
                    {
                        throw new FileNotFoundException($"EXE 中缺少提交 skill 资源：{resource.Key}");
                    }

                    using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                    }
                }
            }

            return cacheRoot;
        }

        private static string GetRepositorySkillTarget(string repositoryPath)
        {
            return string.IsNullOrWhiteSpace(repositoryPath)
                ? null
                : Path.Combine(repositoryPath, ".claude", "skills", SkillDirectoryName);
        }

        private static IEnumerable<string> GetUserSkillTargets()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, ".claude", "skills", SkillDirectoryName);
                yield return Path.Combine(userProfile, ".codex", "skills", SkillDirectoryName);
            }
        }

        private static void SyncSkillDirectory(string sourcePath, string targetPath)
        {
            Directory.CreateDirectory(targetPath);
            CopyDirectory(sourcePath, targetPath);
        }

        private static void CopyDirectory(string sourcePath, string targetPath)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourcePath))
            {
                CopyDirectory(directory, Path.Combine(targetPath, Path.GetFileName(directory)));
            }

            Directory.CreateDirectory(targetPath);
            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)), true);
            }
        }

        private static string ToSafeFileNamePart(string value, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = source
                .Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
                .ToArray();
            var result = new string(chars).Trim('-');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }
    }

    public sealed class AiCommitSkillInfo
    {
        public AiCommitSkillInfo(string sourcePath, string primarySkillPath, string skillMarkdownPath, string workingChangesScriptPath, IReadOnlyList<string> syncedUserSkillPaths, string repositorySkillPath)
        {
            SourcePath = sourcePath;
            PrimarySkillPath = primarySkillPath;
            SkillMarkdownPath = skillMarkdownPath;
            WorkingChangesScriptPath = workingChangesScriptPath;
            SyncedUserSkillPaths = syncedUserSkillPaths;
            RepositorySkillPath = repositorySkillPath;
        }

        public string SourcePath { get; }

        public string PrimarySkillPath { get; }

        public string SkillMarkdownPath { get; }

        public string WorkingChangesScriptPath { get; }

        public IReadOnlyList<string> SyncedUserSkillPaths { get; }

        public string RepositorySkillPath { get; }
    }

    public sealed class AiCommitRunStateInfo
    {
        public AiCommitRunStateInfo(string stateDirectoryPath, string lastChangesJsonPath, string lastChangesModelJsonPath)
        {
            StateDirectoryPath = stateDirectoryPath;
            LastChangesJsonPath = lastChangesJsonPath;
            LastChangesModelJsonPath = lastChangesModelJsonPath;
        }

        public string StateDirectoryPath { get; }

        public string LastChangesJsonPath { get; }

        public string LastChangesModelJsonPath { get; }
    }
}
