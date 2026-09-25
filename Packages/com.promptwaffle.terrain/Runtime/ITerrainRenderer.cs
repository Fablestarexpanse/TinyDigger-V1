namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Anything that draws a <see cref="TerrainGrid"/>. Kept deliberately small so the chunked
    /// mesh implementation can be swapped for a GPU heightfield later without touching the data
    /// model or the edit tools.
    /// </summary>
    public interface ITerrainRenderer
    {
        /// <summary>Flags the cell as needing a rebuild. Cheap; safe to call many times per frame.</summary>
        void MarkDirty(int x, int z);

        /// <summary>Rebuilds whatever has been flagged since the last call. Typically once per frame.</summary>
        void Rebuild();
    }
}
