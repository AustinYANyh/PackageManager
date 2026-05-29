using System;
using System.Collections.Generic;
using System.Linq;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class CodePackageLinkService
    {
        private readonly DataPersistenceService _dataPersistenceService;

        public CodePackageLinkService(DataPersistenceService dataPersistenceService = null)
        {
            _dataPersistenceService = dataPersistenceService ?? ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
        }

        public IReadOnlyList<PackageLinkOption> GetPackageOptions()
        {
            var options = new List<PackageLinkOption>();
            var mainWindow = ServiceLocator.Resolve<global::PackageManager.MainWindow>();
            if (mainWindow?.Packages != null)
            {
                foreach (var package in mainWindow.Packages.Where(package => package != null))
                {
                    AddOption(options, package.ProductName, package.FtpServerPath, package);
                }
            }

            foreach (var item in _dataPersistenceService.GetBuiltInPackageConfigs())
            {
                AddOption(options, item.ProductName, item.FtpServerPath, null);
            }

            foreach (var item in _dataPersistenceService.LoadPackageConfigs())
            {
                AddOption(options, item.ProductName, item.FtpServerPath, null);
            }

            return options
                .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.FirstOrDefault(option => option.Package != null) ?? group.First())
                .OrderBy(option => option.ProductName)
                .ToList();
        }

        public PackageLinkOption FindPackageByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return GetPackageOptions().FirstOrDefault(option =>
                string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public PackageLinkOption SuggestPackage(CodeRepository repository)
        {
            if (repository == null)
            {
                return null;
            }

            var repoText = NormalizeMatchText($"{repository.Name} {repository.Path} {repository.Note}");
            if (string.IsNullOrWhiteSpace(repoText))
            {
                return null;
            }

            var candidates = GetPackageOptions()
                .Select(option => new
                {
                    Option = option,
                    Score = CalculateScore(repoText, NormalizeMatchText(option.ProductName), NormalizeMatchText(option.FtpServerPath)),
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Option.ProductName)
                .ToList();

            var best = candidates.FirstOrDefault();
            if (best == null)
            {
                return null;
            }

            var second = candidates.Skip(1).FirstOrDefault();
            return second == null || best.Score >= second.Score + 2 ? best.Option : null;
        }

        public static string BuildPackageKey(PackageInfo package)
        {
            return package == null ? null : BuildPackageKey(package.ProductName, package.FtpServerPath);
        }

        public static string BuildPackageKey(string productName, string ftpServerPath)
        {
            var name = (productName ?? string.Empty).Trim();
            var path = NormalizeFtpPath(ftpServerPath);
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)
                ? null
                : $"{name}|{path}";
        }

        private static void AddOption(List<PackageLinkOption> options, string productName, string ftpServerPath, PackageInfo package)
        {
            var key = BuildPackageKey(productName, ftpServerPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            options.Add(new PackageLinkOption
            {
                Key = key,
                ProductName = productName?.Trim(),
                FtpServerPath = ftpServerPath?.Trim(),
                Package = package,
            });
        }

        private static string NormalizeFtpPath(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        }

        private static string NormalizeMatchText(string value)
        {
            return (value ?? string.Empty)
                .Replace("（", string.Empty)
                .Replace("）", string.Empty)
                .Replace("(", string.Empty)
                .Replace(")", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }

        private static int CalculateScore(string repositoryText, string productName, string ftpServerPath)
        {
            var score = 0;
            if (!string.IsNullOrWhiteSpace(productName) && repositoryText.Contains(productName))
            {
                score += 5;
            }

            foreach (var token in SplitTokens(productName).Concat(SplitTokens(ftpServerPath)))
            {
                if (token.Length >= 3 && repositoryText.Contains(token))
                {
                    score++;
                }
            }

            return score;
        }

        private static IEnumerable<string> SplitTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            foreach (var token in text.Split(new[] { '/', '\\', '.', ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = NormalizeMatchText(token);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }
}
