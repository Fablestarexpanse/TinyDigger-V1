using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>What each part of a dam piece is drawn with. The kit's material slots, by name.</summary>
    public enum DamSurface
    {
        Concrete,
        ConcreteDark,
        Steel,
        Light,
        SpillWater,
    }

    /// <summary>
    /// The dam kit: the imported pieces (Art/Props/Dam, built by Art/Tools/td_dam.py) and the
    /// material each surface is drawn with. Meshes must be read/write so they can be bent.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Dam Kit", fileName = "DamKit")]
    public sealed class DamKit : ScriptableObject
    {
        [Header("Pieces (the imported models)")]
        public GameObject Bay;
        public GameObject Spillway;
        public GameObject SpillwayWater;
        public GameObject Tower;
        public GameObject Terminal;
        public GameObject Pad;

        [Header("Surfaces")]
        public Material Concrete;
        public Material ConcreteDark;
        public Material Steel;
        public Material Light;
        public Material SpillWater;

        public const int SurfaceCount = 5;

        public Material MaterialFor(DamSurface surface) => surface switch
        {
            DamSurface.ConcreteDark => ConcreteDark,
            DamSurface.Steel => Steel,
            DamSurface.Light => Light,
            DamSurface.SpillWater => SpillWater,
            _ => Concrete,
        };

        /// <summary>
        /// Which surface an imported material slot is, from its name (the Blender material names
        /// are DamConcrete, DamConcreteDark, DamSteel, DamLight and DamSpillWater).
        /// </summary>
        public static DamSurface SurfaceOf(string materialName)
        {
            var name = materialName ?? "";
            if (name.Contains("Dark"))
                return DamSurface.ConcreteDark;
            if (name.Contains("Steel"))
                return DamSurface.Steel;
            if (name.Contains("Light"))
                return DamSurface.Light;
            if (name.Contains("Water"))
                return DamSurface.SpillWater;
            return DamSurface.Concrete;
        }
    }
}
