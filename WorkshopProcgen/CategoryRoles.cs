using System;
using System.Collections.Generic;

namespace WorkshopProcgen
{
    /// <summary>
    /// Turns a Workshop asset's own categories, name and keywords into the native categories it may replace.
    /// New subscriptions pick this up with no config entry. Desks fill table slots, and the same idea applies
    /// to the other groups below.
    /// </summary>
    internal static class CategoryRoles
    {
        private static readonly string[][] Synonyms =
        {
            new[] { "Tables", "Desks" },
            new[] { "Chairs", "Stools" },
            new[] { "Crates", "Chests" },
        };

        private static readonly (string Word, string Category)[] Words =
        {
            ("table", "Tables"),
            ("desk", "Desks"),
            ("chair", "Chairs"),
            ("seat", "Chairs"),
            ("stool", "Stools"),
            ("bench", "Benches"),
            ("barrel", "Barrels"),
            ("vat", "Barrels"),
            ("cask", "Barrels"),
            ("crate", "Crates"),
            ("chest", "Chests"),
            ("container", "Crates"),
            ("pillar", "Columns"),
            ("column", "Columns"),
            ("stair", "Stairs"),
            ("stairs", "Stairs"),
        };

        public static string[] Suggest(string assetName, IList<string> keywords, IList<string> ownCategoryNames,
            Dictionary<string, string> knownNames, StructuralRole role = StructuralRole.None)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ownCategoryNames != null)
            {
                foreach (var name in ownCategoryNames)
                {
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (knownNames.TryGetValue(name, out var canonical))
                        set.Add(canonical);
                }
            }

            foreach (var (word, category) in Words)
            {
                if (!knownNames.TryGetValue(category, out var canonical))
                    continue;
                if (HasWord(assetName, keywords, word))
                    set.Add(canonical);
            }

            var snapshot = new List<string>(set);
            foreach (var name in snapshot)
            {
                foreach (var group in Synonyms)
                {
                    var hit = false;
                    foreach (var member in group)
                    {
                        if (string.Equals(member, name, StringComparison.OrdinalIgnoreCase))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (!hit)
                        continue;
                    foreach (var member in group)
                    {
                        if (knownNames.TryGetValue(member, out var canonical))
                            set.Add(canonical);
                    }
                }
            }

            AddRoleHints(set, assetName, keywords, role, knownNames);

            var list = new List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        // Only categories already known for this block type are added, so an object named "wall" cannot
        // pick up wall categories and a door cannot pick up window categories.
        private static void AddRoleHints(HashSet<string> set, string assetName, IList<string> keywords,
            StructuralRole role, Dictionary<string, string> knownNames)
        {
            string token;
            switch (role)
            {
                case StructuralRole.Floor: token = "floor"; break;
                case StructuralRole.Wall:
                case StructuralRole.PartialWall: token = "wall"; break;
                case StructuralRole.Door: token = "door"; break;
                case StructuralRole.Window: token = "window"; break;
                default: return;
            }
            if (!HasWord(assetName, keywords, token))
                return;
            foreach (var name in knownNames.Values)
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    set.Add(name);
            }
        }

        private static bool HasWord(string assetName, IList<string> keywords, string word)
        {
            if (TokenEquals(assetName, word))
                return true;
            if (keywords == null)
                return false;
            for (int i = 0; i < keywords.Count; i++)
            {
                if (TokenEquals(keywords[i], word))
                    return true;
            }
            return false;
        }

        private static bool TokenEquals(string text, string word)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && !char.IsLetter(text[i]))
                    i++;
                int start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                if (i - start == word.Length && string.Compare(text, start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }
    }
}
