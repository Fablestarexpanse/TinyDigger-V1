namespace TinyDiggers.Terrain
{
    /// <summary>One band of material within a cell's column, with its thickness in metres.</summary>
    public readonly struct Layer
    {
        public readonly MaterialId Material;
        public readonly float Thickness;

        public Layer(MaterialId material, float thickness)
        {
            Material = material;
            Thickness = thickness;
        }

        public override string ToString() => $"{Material} x{Thickness:0.##}m";
    }

    /// <summary>A quantity of one material, as yielded by digging. Cells are 1x1 so volume == metres of thickness.</summary>
    public readonly struct MaterialVolume
    {
        public readonly MaterialId Material;
        public readonly float Volume;

        public MaterialVolume(MaterialId material, float volume)
        {
            Material = material;
            Volume = volume;
        }

        public override string ToString() => $"{Material} x{Volume:0.##}";
    }
}
