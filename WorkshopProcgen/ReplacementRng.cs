using System;
using System.Globalization;
using DungeonAlchemist.RoomGeneration;
using UnityEngine;

namespace WorkshopProcgen
{
    /// <summary>
    /// Deterministic per-object RNG: same room seed + same native object at the same spot = same outcome.
    /// Never draws from the game's SeedRandom, so the native layout is unaffected.
    /// </summary>
    internal static class ReplacementRng
    {
        public static System.Random ForObject(string nativeBlockId, Vector2 mapPosition, float rotation)
        {
            int roomSeed = RoomGenerator.lastGeneratedRandomSeed ?? 0;
            var key = string.Concat(
                nativeBlockId, "|",
                Mathf.RoundToInt(mapPosition.x * 100f).ToString(CultureInfo.InvariantCulture), "|",
                Mathf.RoundToInt(mapPosition.y * 100f).ToString(CultureInfo.InvariantCulture), "|",
                Mathf.RoundToInt(rotation).ToString(CultureInfo.InvariantCulture));
            return new System.Random(Combine(roomSeed, Fnv1a(key)));
        }

        private static int Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        private static int Combine(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u;
                h ^= (uint)b + 0x7F4A7C15u + (h << 6) + (h >> 2);
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}
