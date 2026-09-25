using System.Collections.Generic;
using System.Text;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// How much of each material the crew has dug out of the ground, in solid (in-place) cubic
    /// metres, by the material it was before digging loosened it. Fed from every dig's report;
    /// the toolbar shows the ores. Stockpiles will count what arrives; this counts what left the
    /// ground.
    /// </summary>
    public sealed class MiningLedger
    {
        readonly Dictionary<MaterialId, float> _dug = new Dictionary<MaterialId, float>();

        public int Version { get; private set; }

        public float Dug(MaterialId material) => _dug.TryGetValue(material, out var volume) ? volume : 0f;

        /// <summary>Adds a dig's in-place volumes, indexed by material id.</summary>
        public void Record(float[] inPlaceBySource)
        {
            var changed = false;
            for (var id = 1; id < inPlaceBySource.Length; id++)
            {
                var volume = inPlaceBySource[id];
                if (volume <= 0f)
                    continue;
                var material = new MaterialId((byte)id);
                _dug[material] = Dug(material) + volume;
                changed = true;
            }

            if (changed)
                Version++;
        }

        /// <summary>"Dug: Iron ore 12 m³ · Coal 4 m³", ores only, largest first; empty if none.</summary>
        public string OreSummary(MaterialTable table)
        {
            var ores = new List<KeyValuePair<MaterialId, float>>();
            foreach (var pair in _dug)
                if (TinyDiggersMaterials.IsOre(pair.Key) && pair.Value >= 0.05f)
                    ores.Add(pair);
            if (ores.Count == 0)
                return string.Empty;
            ores.Sort((a, b) => b.Value.CompareTo(a.Value));
            var text = new StringBuilder("Dug: ");
            for (var i = 0; i < ores.Count; i++)
            {
                if (i > 0)
                    text.Append(" · ");
                text.Append(table.Get(ores[i].Key).DisplayName).Append(' ').Append(ores[i].Value.ToString("0")).Append(" m³");
            }

            return text.ToString();
        }
    }
}
