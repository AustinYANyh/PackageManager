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
            var sourcePath = ExtractEmbeddedSkill();

            var targets = GetSkillTargets(repositoryPath).ToList();
            if (targets.Count == 0)
            {
                throw new InvalidOperationException("没有可用的 skill 同步目标。");
            }

            var syncedTargets = new List<string>();
            foreach (var target in targets)
            {
                SyncSkillDirectory(sourcePath, target);
                syncedTargets.Add(target);
            }

            var primarySkillPath = targets.FirstOrDefault(Directory.Exists);
            if (string.IsNullOrWhiteSpace(primarySkillPath))
            {
                throw new DirectoryNotFoundException("提交 skill 同步后仍不可用。");
            }

            var wrapperPath = Path.Combine(primarySkillPath, "scripts", "invoke-working-changes-interactive.ps1");
            if (!File.Exists(wrapperPath))
            {
                throw new FileNotFoundException($"找不到提交采集脚本：{wrapperPath}");
            }

            return new AiCommitSkillInfo(sourcePath, primarySkillPath, wrapperPath, syncedTargets);
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

        private static IEnumerable<string> GetSkillTargets(string repositoryPath)
        {
            if (!string.IsNullOrWhiteSpace(repositoryPath))
            {
                yield return Path.Combine(repositoryPath, ".claude", "skills", SkillDirectoryName);
            }

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
                var name = Path.GetFileName(directory);
                CopyDirectory(directory, Path.Combine(targetPath, name));
            }

            Directory.CreateDirectory(targetPath);
            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)), true);
            }
        }
    }

    public sealed class AiCommitSkillInfo
    {
        public AiCommitSkillInfo(string sourcePath, string primarySkillPath, string workingChangesScriptPath, IReadOnlyList<string> syncedTargets)
        {
            SourcePath = sourcePath;
            PrimarySkillPath = primarySkillPath;
            WorkingChangesScriptPath = workingChangesScriptPath;
            SyncedTargets = syncedTargets;
        }

        public string SourcePath { get; }

        public string PrimarySkillPath { get; }

        public string WorkingChangesScriptPath { get; }

        public IReadOnlyList<string> SyncedTargets { get; }
    }
}
