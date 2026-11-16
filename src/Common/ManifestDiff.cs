using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wolf
{
    // Short human-readable list of what changed between two published packs. The server puts
    // it into server.json as "changelog", and the launcher shows it on the "Что нового" tab.
    public static class ManifestDiff
    {
        public static List<string> Compare(ModManifest previous, ModManifest next)
        {
            List<string> result = new List<string>();
            if (next == null) { return result; }

            Dictionary<string, string> oldVersions = previous == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : previous.modVersions;
            Dictionary<string, string> newVersions = next.modVersions;

            foreach (string mod in next.loadOrder)
            {
                string oldVer, newVer;
                oldVersions.TryGetValue(mod, out oldVer);
                newVersions.TryGetValue(mod, out newVer);
                bool wasThere = previous != null && previous.loadOrder.Contains(mod, StringComparer.OrdinalIgnoreCase);

                if (!wasThere)
                {
                    result.Add("+ " + mod + VersionSuffix(newVer));
                    continue;
                }
                if (!string.Equals(oldVer ?? "", newVer ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add("~ " + mod + " " + (string.IsNullOrEmpty(oldVer) ? "?" : oldVer) +
                               " → " + (string.IsNullOrEmpty(newVer) ? "?" : newVer));
                    continue;
                }
                if (!string.Equals(Signature(previous, mod), Signature(next, mod), StringComparison.Ordinal))
                {
                    result.Add("~ " + mod + " (файлы обновлены)");
                }
            }

            if (previous != null)
            {
                foreach (string mod in previous.loadOrder)
                {
                    if (!next.loadOrder.Contains(mod, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add("- " + mod);
                    }
                }
            }
            return result;
        }

        // Files that belong to one mod folder, hashed in a stable order.
        private static string Signature(ModManifest manifest, string mod)
        {
            if (manifest == null) { return ""; }
            StringBuilder sb = new StringBuilder();
            foreach (ManifestFile f in manifest.files
                .Where(x => x.rel.StartsWith(mod + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.rel, StringComparer.Ordinal))
            {
                sb.Append(f.rel).Append('|').Append(f.size.ToString(CultureInfo.InvariantCulture))
                  .Append('|').Append(f.hash).Append('\n');
            }
            return sb.ToString();
        }

        private static string VersionSuffix(string version)
        {
            return string.IsNullOrEmpty(version) ? "" : " " + version;
        }

        // One line for a chat/log notification, e.g. "обновлено модов: 3 (2 изменено, 1 добавлен)".
        public static string Summary(List<string> changes)
        {
            if (changes == null || changes.Count == 0) { return "без изменений"; }
            int added = 0, removed = 0, updated = 0;
            foreach (string s in changes)
            {
                if (s.StartsWith("+ ")) { added++; }
                else if (s.StartsWith("- ")) { removed++; }
                else { updated++; }
            }
            List<string> parts = new List<string>();
            if (updated > 0) { parts.Add(updated.ToString(CultureInfo.InvariantCulture) + " обновлено"); }
            if (added > 0) { parts.Add(added.ToString(CultureInfo.InvariantCulture) + " добавлено"); }
            if (removed > 0) { parts.Add(removed.ToString(CultureInfo.InvariantCulture) + " убрано"); }
            return string.Join(", ", parts.ToArray());
        }
    }
}
