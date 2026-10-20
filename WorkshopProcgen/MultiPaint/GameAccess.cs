using System.Reflection;
using DungeonAlchemist;
using DungeonAlchemist.Database;
using HarmonyLib;

namespace WorkshopProcgen.MultiPaint
{
    internal static class GameAccess
    {
        private static readonly MethodInfo databaseGetter =
            AccessTools.PropertyGetter(typeof(DatabaseAutoInit), "database");

        internal static bool HasDatabaseGetter => databaseGetter != null;

        /// <summary>Reads the protected <c>DatabaseAutoInit.database</c> of a brush or menu tab.</summary>
        internal static DatabaseConnection Database(DatabaseAutoInit owner)
        {
            if (databaseGetter == null || owner == null)
            {
                return null;
            }
            return databaseGetter.Invoke(owner, null) as DatabaseConnection;
        }
    }
}
