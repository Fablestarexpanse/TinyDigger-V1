using System;
using UnityEngine;

namespace TinyDiggers.Terrain
{
    /// <summary>
    /// The set of materials a <see cref="TerrainGrid"/> can contain, indexed by
    /// <see cref="MaterialId"/> for O(1) lookup in the render hot path.
    /// </summary>
    public sealed class MaterialTable
    {
        readonly MaterialDefinition[] _byId;

        public MaterialTable(params MaterialDefinition[] definitions)
        {
            if (definitions == null)
                throw new ArgumentNullException(nameof(definitions));

            var maxId = 0;
            foreach (var definition in definitions)
            {
                if (definition.Id.IsNone)
                    throw new ArgumentException("MaterialId 0 is reserved for None.", nameof(definitions));
                if (definition.Id.Value > maxId)
                    maxId = definition.Id.Value;
            }

            _byId = new MaterialDefinition[maxId + 1];
            foreach (var definition in definitions)
            {
                if (_byId[definition.Id.Value] != null)
                    throw new ArgumentException($"Duplicate MaterialId {definition.Id}.", nameof(definitions));
                _byId[definition.Id.Value] = definition;
            }

            foreach (var definition in definitions)
                if (!definition.Disturbed.IsNone && !Contains(definition.Disturbed))
                    throw new ArgumentException(
                        $"{definition.DisplayName} is disturbed into id {definition.Disturbed}, which is not in the table.",
                        nameof(definitions));

            Count = definitions.Length;
        }

        public int Count { get; }

        /// <summary>The largest id in the table, so callers can size lookup arrays indexed by id.</summary>
        public int MaxId => _byId.Length - 1;

        public bool Contains(MaterialId id) => id.Value < _byId.Length && _byId[id.Value] != null;

        public MaterialDefinition Get(MaterialId id)
        {
            if (id.Value >= _byId.Length || _byId[id.Value] == null)
                throw new ArgumentOutOfRangeException(nameof(id), $"No material with id {id}.");
            return _byId[id.Value];
        }

        public bool IsDiggable(MaterialId id) => !id.IsNone && Get(id).IsDiggable;

        /// <summary>What <paramref name="id"/> becomes once dug or tipped; itself if it has no disturbed form.</summary>
        public MaterialId GetDisturbed(MaterialId id)
        {
            var disturbed = Get(id).Disturbed;
            return disturbed.IsNone ? id : disturbed;
        }

        // Ids are fixed so that generation, tests and saved data all agree on them.
        public static readonly MaterialId Bedrock = new MaterialId(1);
        public static readonly MaterialId Granite = new MaterialId(2);
        public static readonly MaterialId Rock = new MaterialId(3);
        public static readonly MaterialId Clay = new MaterialId(4);
        public static readonly MaterialId Dirt = new MaterialId(5);
        public static readonly MaterialId Sand = new MaterialId(6);
        public static readonly MaterialId Topsoil = new MaterialId(7);
        public static readonly MaterialId RockLoose = new MaterialId(8);
        public static readonly MaterialId DirtLoose = new MaterialId(9);

        // Ores (slice 10): each in-place ore stands like rock and digs out into its own loose form,
        // so a load of iron ore is still iron ore in the hauler.
        public static readonly MaterialId Coal = new MaterialId(10);
        public static readonly MaterialId CoalLoose = new MaterialId(11);
        public static readonly MaterialId IronOre = new MaterialId(12);
        public static readonly MaterialId IronOreLoose = new MaterialId(13);
        public static readonly MaterialId CopperOre = new MaterialId(14);
        public static readonly MaterialId CopperOreLoose = new MaterialId(15);
        public static readonly MaterialId Limestone = new MaterialId(16);
        public static readonly MaterialId LimestoneLoose = new MaterialId(17);

        /// <summary>The in-place ores, in the order generation and the ore view use.</summary>
        public static readonly MaterialId[] Ores = { Coal, IronOre, CopperOre, Limestone };

        /// <summary>Whether a material is an ore, in place or dug.</summary>
        public static bool IsOre(MaterialId material) => material.Value >= Coal.Value && material.Value <= LimestoneLoose.Value;

        /// <summary>
        /// The seed material set, per TERRAIN_REFERENCE.md section 2. Undisturbed ground stands
        /// steep; once dug it becomes a loose variant with a lower angle of repose and swells by
        /// its bulking factor, so spoil heaps are bigger than the holes they came from and slump
        /// where cut faces hold. Slopes of one 1 m step per cell never slump (a move needs a 2 m
        /// drop), so generated terraces are stable.
        /// </summary>
        /// <summary>
        /// Whether a material stands up rather than slumping: rock, granite, bedrock and the
        /// in-place ores (ore in a rock face is part of the face). A step
        /// taller than one height step is only allowed between two of these — that is what a cliff
        /// is — and the crew works such a face from its foot rather than walking up it.
        /// </summary>
        public static bool IsStone(MaterialId material) =>
            material == Rock || material == Granite || material == Bedrock
            || material == Coal || material == IronOre || material == CopperOre || material == Limestone;

        public static MaterialTable CreateDefault()
        {
            return new MaterialTable(
                // The palette is warm, fairly desaturated, and deliberately high in value: every
                // material sits inside about two stops of the next, so nothing goes black in shade
                // and a shadowed slope still reads as the stuff it is made of rather than as grey.
                // Darker, more saturated colours looked right in a swatch and read as dirt in game.
                new MaterialDefinition(Bedrock, "Bedrock", new Color32(96, 99, 108, 255), 1.00f, 90f, isDiggable: false),
                new MaterialDefinition(Granite, "Granite", new Color32(168, 158, 156, 255), 0.85f, 90f, disturbed: RockLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(Rock, "Rock", new Color32(174, 168, 158, 255), 0.60f, 80f, disturbed: RockLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(Clay, "Clay", new Color32(196, 144, 108, 255), 0.35f, 60f, bulkingFactor: 1.3f),
                new MaterialDefinition(Dirt, "Dirt", new Color32(176, 136, 94, 255), 0.20f, 50f, disturbed: DirtLoose, bulkingFactor: 1.25f),
                new MaterialDefinition(Sand, "Sand", new Color32(228, 210, 170, 255), 0.15f, 34f, bulkingFactor: 1.1f, isLoose: true),
                new MaterialDefinition(Topsoil, "Topsoil", new Color32(140, 154, 100, 255), 0.10f, 50f, disturbed: Dirt, bulkingFactor: 1.25f),
                new MaterialDefinition(RockLoose, "Loose rock", new Color32(182, 175, 164, 255), 0.30f, 38f, isLoose: true),
                new MaterialDefinition(DirtLoose, "Loose dirt", new Color32(190, 152, 110, 255), 0.10f, 32f, isLoose: true),
                new MaterialDefinition(Coal, "Coal", new Color32(40, 40, 44, 255), 0.45f, 80f, disturbed: CoalLoose, bulkingFactor: 1.4f),
                new MaterialDefinition(CoalLoose, "Loose coal", new Color32(52, 52, 56, 255), 0.20f, 38f, isLoose: true),
                new MaterialDefinition(IronOre, "Iron ore", new Color32(140, 72, 48, 255), 0.70f, 85f, disturbed: IronOreLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(IronOreLoose, "Loose iron ore", new Color32(150, 82, 56, 255), 0.30f, 38f, isLoose: true),
                new MaterialDefinition(CopperOre, "Copper ore", new Color32(78, 140, 118, 255), 0.65f, 85f, disturbed: CopperOreLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(CopperOreLoose, "Loose copper ore", new Color32(88, 148, 124, 255), 0.30f, 38f, isLoose: true),
                new MaterialDefinition(Limestone, "Limestone", new Color32(214, 206, 184, 255), 0.50f, 85f, disturbed: LimestoneLoose, bulkingFactor: 1.45f),
                new MaterialDefinition(LimestoneLoose, "Loose limestone", new Color32(222, 214, 192, 255), 0.25f, 36f, isLoose: true));
        }
    }
}
