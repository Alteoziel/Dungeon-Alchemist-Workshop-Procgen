using System;
using System.Collections.Generic;
using System.Linq;
using DungeonAlchemist.Config;

namespace WorkshopProcgen.MultiPaint
{
    internal sealed class PaintSetEntry
    {
        internal BuildingBlockVariant Variant { get; }
        internal string Name { get; }
        internal string Key => Variant.GetHash();
        internal string BlockId => Variant.block.Id;

        internal PaintSetEntry(BuildingBlockVariant variant, string name)
        {
            Variant = new BuildingBlockVariant(variant.block, variant.variantId, variant.variantIndex);
            Name = string.IsNullOrEmpty(name) ? variant.block.name : name;
        }
    }

    /// <summary>Main-thread-only list of picker entries the user has multi-selected for painting.</summary>
    internal static class PaintSet
    {
        private static readonly List<PaintSetEntry> entries = new List<PaintSetEntry>();
        private static readonly Random random = new Random();

        internal static event Action Changed;

        internal static IReadOnlyList<PaintSetEntry> Entries => entries;
        internal static int Count => entries.Count;

        internal static bool Contains(string blockId)
        {
            return blockId != null && entries.Any(e => e.BlockId == blockId);
        }

        /// <returns>true if the entry is now in the set, false if it was removed.</returns>
        internal static bool Toggle(BuildingBlockVariant variant, string name)
        {
            string key = variant.GetHash();
            int index = entries.FindIndex(e => e.Key == key);
            bool added;
            if (index >= 0)
            {
                entries.RemoveAt(index);
                added = false;
            }
            else
            {
                entries.Add(new PaintSetEntry(variant, name));
                added = true;
            }
            Changed?.Invoke();
            return added;
        }

        internal static void Clear()
        {
            if (entries.Count == 0)
            {
                return;
            }
            entries.Clear();
            Changed?.Invoke();
        }

        internal static PaintSetEntry PickRandom()
        {
            return entries.Count == 0 ? null : entries[random.Next(entries.Count)];
        }

        internal static List<string> DistinctBlockIds()
        {
            return entries.Select(e => e.BlockId).Distinct().ToList();
        }
    }
}
