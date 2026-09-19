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

        /// <summary>
        /// The seed material set, per TERRAIN_REFERENCE.md section 2. Undisturbed ground stands
        /// steep; once dug it becomes a loose variant with a lower angle of repose and swells by
        /// its bulking factor, so spoil heaps are bigger than the holes they came from and slump
        /// where cut faces hold. Slopes of one 1 m step per cell never slump (a move needs a 2 m
        /// drop), so generated terraces are stable.
        /// </summary>
        public static MaterialTable CreateDefault()
        {
            return new MaterialTable(
                new MaterialDefinition(Bedrock, "Bedrock", new Color32(52, 52, 58, 255), 1.00f, 90f, isDiggable: false),
                // Rock colours are kept darker than they look in a swatch: lit by the full sun plus
                // ambient, lighter greys rendered as near-white and a cut stopped reading as rock.
                new MaterialDefinition(Granite, "Granite", new Color32(96, 92, 94, 255), 0.85f, 90f, disturbed: RockLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(Rock, "Rock", new Color32(112, 110, 106, 255), 0.60f, 80f, disturbed: RockLoose, bulkingFactor: 1.5f),
                new MaterialDefinition(Clay, "Clay", new Color32(166, 106, 72, 255), 0.35f, 60f, bulkingFactor: 1.3f),
                new MaterialDefinition(Dirt, "Dirt", new Color32(122, 88, 60, 255), 0.20f, 50f, disturbed: DirtLoose, bulkingFactor: 1.25f),
                new MaterialDefinition(Sand, "Sand", new Color32(214, 195, 140, 255), 0.15f, 34f, bulkingFactor: 1.1f, isLoose: true),
                new MaterialDefinition(Topsoil, "Topsoil", new Color32(86, 106, 58, 255), 0.10f, 50f, disturbed: Dirt, bulkingFactor: 1.25f),
                new MaterialDefinition(RockLoose, "Loose rock", new Color32(130, 125, 118, 255), 0.30f, 38f, isLoose: true),
                new MaterialDefinition(DirtLoose, "Loose dirt", new Color32(148, 110, 76, 255), 0.10f, 32f, isLoose: true));
        }
    }
}
