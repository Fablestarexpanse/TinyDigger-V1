using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Ground from a Unity <see cref="Terrain"/>: the zone reads the terrain's height at each cell
    /// centre. Cells off the terrain are walls. Call <see cref="Refresh"/> after editing the
    /// heightmap at runtime.
    /// </summary>
    [AddComponentMenu("PromptWaffle/Dynamic Water/Terrain Water Ground")]
    public sealed class TerrainWaterGround : WaterGroundProvider
    {
        [SerializeField] Terrain _terrain;

        public override void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into)
        {
            var terrain = _terrain != null ? _terrain : GetComponent<Terrain>();
            var i = 0;
            for (var z = region.yMin; z < region.yMax; z++)
            {
                for (var x = region.xMin; x < region.xMax; x++)
                {
                    var centre = desc.CellCentre(x, z);
                    var world = new Vector3(centre.x, 0f, centre.y);
                    if (terrain == null || !Covers(terrain, world))
                        into[i++] = WaterGround.Wall;
                    else
                        into[i++] = terrain.SampleHeight(world) + terrain.transform.position.y;
                }
            }
        }

        /// <summary>Tells the zone the whole terrain changed.</summary>
        public void Refresh()
        {
            var terrain = _terrain != null ? _terrain : GetComponent<Terrain>();
            if (terrain == null)
                return;
            var p = terrain.transform.position;
            var size = terrain.terrainData.size;
            RaiseChanged(new Rect(p.x, p.z, size.x, size.z));
        }

        static bool Covers(Terrain terrain, Vector3 world)
        {
            var p = terrain.transform.position;
            var size = terrain.terrainData.size;
            return world.x >= p.x && world.z >= p.z && world.x <= p.x + size.x && world.z <= p.z + size.z;
        }
    }
}
