namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Constants every part of the world agrees on.
    ///
    /// Heights are metres above sea level, which is the datum: a seabed near the rim sits at about
    /// -15 m and a peak at about +60 m. A column's own layers start at <see cref="TerrainGrid.Datum"/>
    /// below that, which is where the bedrock base goes, so nothing can be dug through the disc.
    /// </summary>
    public static class World
    {
        /// <summary>The height the sea stands at. Everything below this is under water.</summary>
        public const float SeaLevel = 0f;
    }
}
