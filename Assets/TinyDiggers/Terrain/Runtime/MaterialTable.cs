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

            Count = definitions.Length;
        }

        public int Count { get; }

        public MaterialDefinition Get(MaterialId id)
        {
            if (id.Value >= _byId.Length || _byId[id.Value] == null)
                throw new ArgumentOutOfRangeException(nameof(id), $"No material with id {id}.");
            return _byId[id.Value];
        }

        public bool IsDiggable(MaterialId id) => !id.IsNone && Get(id).IsDiggable;

        // Ids are fixed so that generation, tests and saved data all agree on them.
        public static readonly MaterialId Bedrock = new MaterialId(1);
        public static readonly MaterialId Granite = new MaterialId(2);
        public static readonly MaterialId Rock = new MaterialId(3);
        public static readonly MaterialId Clay = new MaterialId(4);
        public static readonly MaterialId Dirt = new MaterialId(5);
        public static readonly MaterialId Sand = new MaterialId(6);
        public static readonly MaterialId Topsoil = new MaterialId(7);

        /// <summary>The seed material set for the terrain slice, bottom of the world upwards.</summary>
        public static MaterialTable CreateDefault()
        {
            return new MaterialTable(
                new MaterialDefinition(Bedrock, "Bedrock", new Color32(52, 52, 58, 255), 1.00f, 90f, isDiggable: false),
                new MaterialDefinition(Granite, "Granite", new Color32(128, 122, 124, 255), 0.85f, 90f),
                new MaterialDefinition(Rock, "Rock", new Color32(150, 148, 142, 255), 0.60f, 45f),
                new MaterialDefinition(Clay, "Clay", new Color32(166, 106, 72, 255), 0.35f, 40f),
                new MaterialDefinition(Dirt, "Dirt", new Color32(122, 88, 60, 255), 0.20f, 35f),
                new MaterialDefinition(Sand, "Sand", new Color32(214, 195, 140, 255), 0.15f, 32f),
                new MaterialDefinition(Topsoil, "Topsoil", new Color32(86, 106, 58, 255), 0.10f, 35f));
        }
    }
}
