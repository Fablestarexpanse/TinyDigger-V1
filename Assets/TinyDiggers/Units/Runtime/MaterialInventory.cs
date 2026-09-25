using System;
using System.Collections.Generic;
using PromptWaffle.Terrain;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Loose material carried by something: a crew's scoop now, a hauler's bed later. Holds
    /// loose volume in m³ per material up to a fixed <see cref="Capacity"/>.
    ///
    /// Contents are a stack in the order they went in, so the most recently dug material is tipped
    /// first (<see cref="TryPeekTop"/>, <see cref="RemoveFromTop"/>). Adding the same material
    /// as the current top merges into it. A per-material total is kept alongside for lookups.
    /// </summary>
    public sealed class MaterialInventory
    {
        public const float DefaultCapacity = 5f;

        /// <summary>Volumes this close count as equal; loose volumes are sums of bulked floats.</summary>
        const float Epsilon = 1e-4f;

        readonly List<MaterialVolume> _stack = new List<MaterialVolume>();
        readonly Dictionary<MaterialId, float> _byMaterial = new Dictionary<MaterialId, float>();

        public MaterialInventory(float capacity = DefaultCapacity)
        {
            if (!(capacity > 0f))
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
            Capacity = capacity;
        }

        /// <summary>Loose m³ this inventory can hold.</summary>
        public float Capacity { get; }

        /// <summary>Loose m³ held.</summary>
        public float Total { get; private set; }

        public float Remaining => Math.Max(0f, Capacity - Total);

        public bool IsEmpty => _stack.Count == 0;

        /// <summary>Changes on every mutation, so displays can redraw only when something changed.</summary>
        public int Version { get; private set; }

        /// <summary>Contents, bottom (oldest) to top (newest).</summary>
        public IReadOnlyList<MaterialVolume> Stack => _stack;

        public float GetVolume(MaterialId material) => _byMaterial.TryGetValue(material, out var volume) ? volume : 0f;

        public bool CanFit(float volume) => volume <= Remaining + Epsilon;

        /// <summary>Adds all of <paramref name="volume"/> if it fits, otherwise nothing. Returns whether it was added.</summary>
        public bool TryAdd(MaterialId material, float volume)
        {
            Validate(material, volume);
            if (!CanFit(volume))
                return false;
            if (volume <= Epsilon)
                return true;

            if (_stack.Count > 0 && _stack[_stack.Count - 1].Material == material)
            {
                var top = _stack[_stack.Count - 1];
                _stack[_stack.Count - 1] = new MaterialVolume(material, top.Volume + volume);
            }
            else
            {
                _stack.Add(new MaterialVolume(material, volume));
            }

            _byMaterial[material] = GetVolume(material) + volume;
            Total += volume;
            Version++;
            return true;
        }

        /// <summary>Adds <paramref name="volume"/>; throws if it does not fit. Use <see cref="TryAdd"/> when overflow is expected.</summary>
        public void Add(MaterialId material, float volume)
        {
            if (!TryAdd(material, volume))
                throw new InvalidOperationException(
                    $"{volume:0.###} m³ does not fit: {Remaining:0.###} of {Capacity:0.###} m³ free.");
        }

        /// <summary>
        /// Removes up to <paramref name="volume"/> of <paramref name="material"/>, newest first,
        /// wherever it sits in the stack. Returns how much was removed.
        /// </summary>
        public float Remove(MaterialId material, float volume)
        {
            Validate(material, volume);
            var removed = 0f;
            for (var i = _stack.Count - 1; i >= 0 && removed < volume - Epsilon; i--)
            {
                if (_stack[i].Material != material)
                    continue;
                removed += Take(i, volume - removed);
            }

            MergeNeighbours();
            return removed;
        }

        public bool TryPeekTop(out MaterialVolume top)
        {
            if (_stack.Count == 0)
            {
                top = default;
                return false;
            }

            top = _stack[_stack.Count - 1];
            return true;
        }

        /// <summary>Removes up to <paramref name="volume"/> from the top entry only. Returns how much was removed.</summary>
        public float RemoveFromTop(float volume)
        {
            if (!(volume >= 0f))
                throw new ArgumentOutOfRangeException(nameof(volume));
            if (_stack.Count == 0)
                return 0f;
            return Take(_stack.Count - 1, volume);
        }

        /// <summary>Takes up to <paramref name="volume"/> from entry <paramref name="index"/>, dropping it if emptied.</summary>
        float Take(int index, float volume)
        {
            var entry = _stack[index];
            var taken = Math.Min(volume, entry.Volume);
            var left = entry.Volume - taken;
            if (left <= Epsilon)
            {
                taken = entry.Volume;
                _stack.RemoveAt(index);
            }
            else
            {
                _stack[index] = new MaterialVolume(entry.Material, left);
            }

            var remaining = GetVolume(entry.Material) - taken;
            if (remaining <= Epsilon)
                _byMaterial.Remove(entry.Material);
            else
                _byMaterial[entry.Material] = remaining;

            Total = _stack.Count == 0 ? 0f : Math.Max(0f, Total - taken);
            Version++;
            return taken;
        }

        /// <summary>After removing from the middle, two entries of the same material can end up adjacent.</summary>
        void MergeNeighbours()
        {
            for (var i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Material != _stack[i - 1].Material)
                    continue;
                _stack[i - 1] = new MaterialVolume(_stack[i].Material, _stack[i - 1].Volume + _stack[i].Volume);
                _stack.RemoveAt(i);
            }
        }

        static void Validate(MaterialId material, float volume)
        {
            if (material.IsNone)
                throw new ArgumentException("Cannot hold MaterialId.None.", nameof(material));
            if (!(volume >= 0f))
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be zero or positive.");
        }
    }
}
