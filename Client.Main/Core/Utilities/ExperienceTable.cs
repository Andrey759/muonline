namespace Client.Main.Core.Utilities
{
    /// <summary>
    /// Total experience needed to reach a character level, as the OpenMU server computes it (Season 6 table:
    /// above level 255 an extra term applies). The client must use the same table, otherwise the remaining
    /// experience drifts away from the server after every level-up until the next relog.
    /// </summary>
    public static class ExperienceTable
    {
        public static ulong TotalForLevel(int level)
        {
            if (level <= 1)
                return 0;

            long l = level;
            long exp = 10 * (l + 8) * (l - 1) * (l - 1);
            if (l >= 256)
                exp += 1000 * (l - 247) * (l - 256) * (l - 256);
            return (ulong)exp;
        }

        /// <summary>Experience total at which the given level ends (= start of the next level).</summary>
        public static ulong ForNextLevel(int level) => TotalForLevel(level + 1);
    }
}
