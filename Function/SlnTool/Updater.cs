using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PackageManager.Function.SlnTool
{
    internal class Updater
    {
        public bool UpdateDependencies(string slnPath)
        {
            var slnDir = System.IO.Path.GetDirectoryName(slnPath)!;
            var lines = File.ReadAllLines(slnPath).ToList();
            var projects = ParseSolutionProjects(lines);
            var nameToProject = projects.Values.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            var pathToProject = projects.Values.ToDictionary(x => NormalizePath(x.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects.Values)
            {
                var csprojFullForName = System.IO.Path.GetFullPath(System.IO.Path.Combine(slnDir, p.RelativePath));
                string asmName = System.IO.Path.GetFileNameWithoutExtension(p.RelativePath);
                if (File.Exists(csprojFullForName))
                {
                    using var s = File.OpenRead(csprojFullForName);
                    var x = XDocument.Load(s);
                    var ns = x.Root!.Name.Namespace;
                    var asm = x.Descendants(ns + "AssemblyName").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    var rootNs = x.Descendants(ns + "RootNamespace").Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    if (!string.IsNullOrWhiteSpace(asm)) asmName = asm!;
                    if (!string.IsNullOrWhiteSpace(rootNs) && !nameToProject.ContainsKey(rootNs!)) nameToProject[rootNs!] = p;
                }
                if (!nameToProject.ContainsKey(asmName)) nameToProject[asmName] = p;
            }
            var desiredHard = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var desiredSoft = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects.Values)
            {
                var csprojFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(slnDir, p.RelativePath));
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var depGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var importNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(csprojFull))
                {
                    ExtractCandidatesFromCsproj(slnDir, csprojFull, candidates, depGuids, importNames, projects);
                    ExtractCandidatesFromSources(System.IO.Path.GetDirectoryName(csprojFull)!, candidates, nameToProject);
                }
                var mapped = MapCandidatesToGuids(candidates, nameToProject);
                var importMapped = MapCandidatesToGuids(importNames, nameToProject);
                if (mapped.Contains(p.Guid)) mapped.Remove(p.Guid);
                var hardSet = new HashSet<string>(depGuids, StringComparer.OrdinalIgnoreCase);
                foreach (var g in importMapped) hardSet.Add(g);
                desiredHard[p.Guid] = hardSet;
                desiredSoft[p.Guid] = mapped;
            }
            var finalDeps = ResolveAcyclicDeps(desiredHard, desiredSoft, projects.Keys);
            var changed = false;
            foreach (var guid in projects.Keys.ToList())
            {
                projects = ParseSolutionProjects(lines);
                if (!projects.TryGetValue(guid, out var p)) continue;
                var target = finalDeps.TryGetValue(p.Guid, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!SetEqual(p.DependencyGuids, target))
                {
                    UpsertProjectSection(lines, p, target);
                    changed = true;
                }
            }
            if (changed)
            {
                File.WriteAllLines(slnPath, lines);
            }
            return changed;
        }

        private static Dictionary<string, SlnProject> ParseSolutionProjects(List<string> lines)
        {
            var dict = new Dictionary<string, SlnProject>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.StartsWith("Project(", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, "^Project\\(\"[^\"]+\"\\)\\s=\\s\"([^\"]+)\",\\s\"([^\"]+)\",\\s\"\\{([A-Fa-f0-9\\-]+)\\}\"");
                    if (m.Success)
                    {
                        var name = m.Groups[1].Value;
                        var rel = m.Groups[2].Value;
                        var guid = m.Groups[3].Value.ToUpperInvariant();
                        var start = i;
                        var end = i;
                        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var j = i + 1;
                        while (j < lines.Count)
                        {
                            if (lines[j].StartsWith("EndProject", StringComparison.Ordinal))
                            {
                                end = j;
                                break;
                            }
                            if (lines[j].Contains("ProjectSection(ProjectDependencies)"))
                            {
                                var k = j + 1;
                                while (k < lines.Count && !lines[k].StartsWith("\tEndProjectSection", StringComparison.Ordinal))
                                {
                                    var dm = Regex.Match(lines[k], "\\{([A-Fa-f0-9\\-]+)\\}\\s=\\s\\{([A-Fa-f0-9\\-]+)\\}");
                                    if (dm.Success)
                                    {
                                        deps.Add(dm.Groups[1].Value.ToUpperInvariant());
                                    }
                                    k++;
                                }
                                j = k;
                            }
                            j++;
                        }
                        var sp = new SlnProject
                        {
                            Name = name,
                            RelativePath = rel,
                            Guid = guid,
                            StartIndex = start,
                            EndIndex = end,
                            DependencyGuids = deps
                        };
                        dict[guid] = sp;
                        i = end;
                    }
                }
                i++;
            }
            return dict;
        }

        private static void ExtractCandidatesFromCsproj(string slnDir, string csprojFull, HashSet<string> candidates, HashSet<string> depGuids, HashSet<string> importNames, Dictionary<string, SlnProject> slnProjects)
        {
            XDocument x;
            using (var s = File.OpenRead(csprojFull))
            {
                x = XDocument.Load(s);
            }
            var ns = x.Root!.Name.Namespace;
            var imports = x.Descendants(ns + "Import").Select(e => (string?)e.Attribute("Project")).Where(v => !string.IsNullOrWhiteSpace(v));
            foreach (var p in imports)
            {
                var pkg = ExtractPackageNameFromPath(p!);
                if (!string.IsNullOrEmpty(pkg)) candidates.Add(pkg);
                var tname = ExtractTargetNameFromPath(p!);
                if (!string.IsNullOrEmpty(tname)) candidates.Add(tname);
                if (!string.IsNullOrEmpty(pkg)) importNames.Add(pkg);
                if (!string.IsNullOrEmpty(tname)) importNames.Add(tname);
            }
            var refs = x.Descendants(ns + "Reference").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v));
            foreach (var r in refs)
            {
                var baseName = ExtractBaseName(r!);
                if (!string.IsNullOrEmpty(baseName)) candidates.Add(baseName);
            }
            var pkgs = x.Descendants(ns + "PackageReference").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v));
            foreach (var prf in pkgs)
            {
                var baseName = ExtractBaseName(prf!);
                if (!string.IsNullOrEmpty(baseName)) candidates.Add(baseName);
                if (!string.IsNullOrEmpty(baseName)) importNames.Add(baseName);
            }
            var hintPaths = x.Descendants(ns + "HintPath").Select(e => e.Value).Where(v => !string.IsNullOrWhiteSpace(v));
            foreach (var hp in hintPaths)
            {
                var pkg = ExtractPackageNameFromPath(hp);
                if (!string.IsNullOrEmpty(pkg)) candidates.Add(pkg);
                var baseName = ExtractBaseNameFromHint(hp);
                if (!string.IsNullOrEmpty(baseName)) candidates.Add(baseName);
                if (!string.IsNullOrEmpty(pkg)) importNames.Add(pkg);
                if (!string.IsNullOrEmpty(baseName)) importNames.Add(baseName);
            }
            var projRefs = x.Descendants(ns + "ProjectReference").Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v));
            foreach (var pr in projRefs)
            {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(csprojFull)!, pr!));
                foreach (var sp in slnProjects.Values)
                {
                    var pfull = System.IO.Path.GetFullPath(System.IO.Path.Combine(slnDir, sp.RelativePath));
                    if (string.Equals(pfull, full, StringComparison.OrdinalIgnoreCase))
                    {
                        depGuids.Add(sp.Guid);
                        break;
                    }
                }
            }
        }

        private static HashSet<string> MapCandidatesToGuids(HashSet<string> candidates, Dictionary<string, SlnProject> nameToProject)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (nameToProject.TryGetValue(c, out var p))
                {
                    set.Add(p.Guid);
                    continue;
                }
                var nc = NormalizeName(c);
                foreach (var kv in nameToProject)
                {
                    if (string.Equals(nc, NormalizeName(kv.Key), StringComparison.Ordinal))
                    {
                        set.Add(kv.Value.Guid);
                        break;
                    }
                }
            }
            return set;
        }

        private static void ExtractCandidatesFromSources(string projectDir, HashSet<string> candidates, Dictionary<string, SlnProject> nameToProject)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
                {
                    string text;
                    using (var s = File.OpenRead(file))
                    using (var sr = new StreamReader(s))
                    {
                        text = sr.ReadToEnd();
                    }
                    foreach (Match m in Regex.Matches(text, @"using\s+([A-Za-z_][A-Za-z0-9_\.]*)\s*;"))
                    {
                        var ns = m.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(ns)) continue;
                        var top = ns.Split('.')[0];
                        if (nameToProject.ContainsKey(ns)) candidates.Add(ns);
                        if (nameToProject.ContainsKey(top)) candidates.Add(top);
                    }
                }
            }
            catch { }
        }

        private static void UpsertProjectSection(List<string> lines, SlnProject p, HashSet<string> targetGuids)
        {
            var insertIndex = p.StartIndex + 1;
            var sectionStart = -1;
            var sectionEnd = -1;
            for (var i = p.StartIndex + 1; i < p.EndIndex; i++)
            {
                if (lines[i].Contains("ProjectSection(ProjectDependencies)")) sectionStart = i;
                if (sectionStart != -1 && lines[i].StartsWith("\tEndProjectSection", StringComparison.Ordinal)) { sectionEnd = i; break; }
            }
            if (sectionStart == -1)
            {
                lines.Insert(insertIndex, "\tProjectSection(ProjectDependencies) = postProject");
                insertIndex++;
                foreach (var g in targetGuids)
                {
                    lines.Insert(insertIndex, "\t\t{" + g.ToUpperInvariant() + "} = {" + g.ToUpperInvariant() + "}");
                    insertIndex++;
                }
                lines.Insert(insertIndex, "\tEndProjectSection");
                p.DependencyGuids = new HashSet<string>(targetGuids.Select(x => x.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var k = sectionStart + 1;
                while (k < sectionEnd)
                {
                    lines.RemoveAt(k);
                    sectionEnd--;
                }
                foreach (var g in targetGuids)
                {
                    lines.Insert(k, "\t\t{" + g.ToUpperInvariant() + "} = {" + g.ToUpperInvariant() + "}");
                    k++;
                }
                p.DependencyGuids = new HashSet<string>(targetGuids.Select(x => x.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool SetEqual(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var x in a) if (!b.Contains(x)) return false;
            return true;
        }

        private static Dictionary<string, HashSet<string>> ResolveAcyclicDeps(
            Dictionary<string, HashSet<string>> hard,
            Dictionary<string, HashSet<string>> soft,
            IEnumerable<string> nodes)
        {
            var hardCopy = hard.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var softCopy = soft.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            int guard = 0;
            while (guard++ < 10)
            {
                var adj = CombineAdj(hardCopy, softCopy, nodes);
                var processed = TopoCount(adj, nodes);
                var total = nodes.Count();
                if (processed == total) return adj;
                var cycleNodes = GetCycleNodes(adj, nodes);
                var removedAny = false;
                foreach (var u in cycleNodes)
                {
                    if (softCopy.TryGetValue(u, out var set))
                    {
                        var removed = set.RemoveWhere(v => cycleNodes.Contains(v));
                        if (removed > 0) removedAny = true;
                    }
                }
                if (!removedAny)
                {
                    foreach (var u in cycleNodes)
                    {
                        if (softCopy.TryGetValue(u, out var set) && set.Count > 0)
                        {
                            set.Clear();
                            removedAny = true;
                        }
                    }
                }
                if (!removedAny) break;
            }
            return CombineAdj(hardCopy, new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), nodes);
        }

        private static Dictionary<string, HashSet<string>> CombineAdj(
            Dictionary<string, HashSet<string>> hard,
            Dictionary<string, HashSet<string>> soft,
            IEnumerable<string> nodes)
        {
            var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (hard.TryGetValue(n, out var h)) foreach (var v in h) if (!string.Equals(v, n, StringComparison.OrdinalIgnoreCase)) set.Add(v);
                if (soft.TryGetValue(n, out var s)) foreach (var v in s) if (!string.Equals(v, n, StringComparison.OrdinalIgnoreCase)) set.Add(v);
                adj[n] = set;
            }
            return adj;
        }

        private static int TopoCount(Dictionary<string, HashSet<string>> adj, IEnumerable<string> nodes)
        {
            var indeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes) indeg[n] = 0;
            foreach (var kv in adj)
            {
                foreach (var v in kv.Value)
                {
                    if (indeg.ContainsKey(v)) indeg[v]++;
                }
            }
            var q = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var count = 0;
            var indegLocal = new Dictionary<string, int>(indeg, StringComparer.OrdinalIgnoreCase);
            while (q.Count > 0)
            {
                var u = q.Dequeue();
                count++;
                foreach (var v in adj[u])
                {
                    if (!indegLocal.ContainsKey(v)) continue;
                    indegLocal[v]--;
                    if (indegLocal[v] == 0) q.Enqueue(v);
                }
            }
            return count;
        }

        private static HashSet<string> GetCycleNodes(Dictionary<string, HashSet<string>> adj, IEnumerable<string> nodes)
        {
            var indeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes) indeg[n] = 0;
            foreach (var kv in adj)
            {
                foreach (var v in kv.Value)
                {
                    if (indeg.ContainsKey(v)) indeg[v]++;
                }
            }
            var q = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var indegLocal = new Dictionary<string, int>(indeg, StringComparer.OrdinalIgnoreCase);
            while (q.Count > 0)
            {
                var u = q.Dequeue();
                foreach (var v in adj[u])
                {
                    if (!indegLocal.ContainsKey(v)) continue;
                    indegLocal[v]--;
                    if (indegLocal[v] == 0) q.Enqueue(v);
                }
                indegLocal.Remove(u);
            }
            return new HashSet<string>(indegLocal.Keys, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string rel)
        {
            return rel.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
        }

        private static string ExtractPackageNameFromPath(string path)
        {
            var idx = path.IndexOf(@"$(HWNuGetPackages)\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;
            var s = path.Substring(idx + "$(HWNuGetPackages)".Length + 1);
            var parts = s.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;
            return parts[0];
        }

        private static string ExtractTargetNameFromPath(string path)
        {
            var idx = path.LastIndexOf("\\build\\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;
            var s = path.Substring(idx + "\\build\\".Length);
            var name = s.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - ".targets".Length) : s;
            return name;
        }

        private static string ExtractBaseName(string include)
        {
            if (string.IsNullOrWhiteSpace(include)) return string.Empty;
            var token = include.Split(',')[0].Trim();
            if (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) token = token.Substring(0, token.Length - 4);
            return token;
        }

        private static string NormalizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var t = s.Trim();
            if (t.StartsWith("HW", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (t.EndsWith("UI", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 2);
            t = t.Replace(".", "").Replace("-", "").Replace("_", "");
            return t.ToLowerInvariant();
        }

        private static string ExtractBaseNameFromHint(string hint)
        {
            var file = System.IO.Path.GetFileName(hint);
            if (string.IsNullOrEmpty(file)) return string.Empty;
            var idx = file.IndexOf('.');
            var name = idx > 0 ? file.Substring(0, idx) : file;
            return name;
        }
    }

    internal class SlnProject
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Guid { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public HashSet<string> DependencyGuids { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
