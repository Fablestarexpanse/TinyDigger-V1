using System;
using System.Collections.Generic;
using TinyDiggers.Units;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Undo and redo for the player's designation edits (Slice 17): a stroke, a rectangle, a road,
    /// a clear. Up to <see cref="Capacity"/> steps back.
    ///
    /// An edit is recorded between <see cref="Begin"/> and <see cref="Commit"/>: every cell the map
    /// is about to change in that time has its state taken first, once. Anything the map does
    /// outside a recording is not the player's and is never undone: the crew meeting a
    /// designation, a unit cutting itself a ramp. Undoing puts each recorded cell back as it was,
    /// and takes the cell as it is now for redo.
    /// </summary>
    public sealed class DesignationHistory : IDisposable
    {
        /// <summary>Steps kept.</summary>
        public const int Capacity = 50;

        sealed class Step
        {
            public string Label;
            public readonly List<int> Cells = new List<int>();
            public readonly List<DesignationCellState> States = new List<DesignationCellState>();
        }

        readonly DesignationMap _map;
        readonly LinkedList<Step> _undo = new LinkedList<Step>();
        readonly Stack<Step> _redo = new Stack<Step>();
        readonly HashSet<int> _seen = new HashSet<int>();
        Step _recording;
        bool _replaying;

        public DesignationHistory(DesignationMap map)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _map.Changing += OnChanging;
        }

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public int UndoCount => _undo.Count;

        /// <summary>What the next undo would take back, for the toolbar tooltip.</summary>
        public string NextUndo => _undo.Count > 0 ? _undo.Last.Value.Label : "";

        public string NextRedo => _redo.Count > 0 ? _redo.Peek().Label : "";

        public bool IsRecording => _recording != null;

        /// <summary>Starts recording an edit. A recording already open is committed first.</summary>
        public void Begin(string label)
        {
            if (_recording != null)
                Commit();
            _recording = new Step { Label = label };
            _seen.Clear();
        }

        /// <summary>
        /// Ends the edit. One that changed nothing is dropped; one that did becomes the next undo
        /// and clears the redo stack, as a new edit does in any editor.
        /// </summary>
        public void Commit()
        {
            var step = _recording;
            _recording = null;
            _seen.Clear();
            if (step == null || step.Cells.Count == 0)
                return;
            _undo.AddLast(step);
            if (_undo.Count > Capacity)
                _undo.RemoveFirst();
            _redo.Clear();
        }

        /// <summary>Takes back the last edit. Returns its label, or null if there was nothing to undo.</summary>
        public string Undo()
        {
            if (_recording != null)
                Commit();
            if (_undo.Count == 0)
                return null;
            var step = _undo.Last.Value;
            _undo.RemoveLast();
            _redo.Push(Swap(step));
            return step.Label;
        }

        /// <summary>Does the last undone edit again. Returns its label, or null if there was nothing to redo.</summary>
        public string Redo()
        {
            if (_recording != null)
                Commit();
            if (_redo.Count == 0)
                return null;
            var step = _redo.Pop();
            _undo.AddLast(Swap(step));
            return step.Label;
        }

        /// <summary>Forgets everything, for a new island.</summary>
        public void Clear()
        {
            _recording = null;
            _seen.Clear();
            _undo.Clear();
            _redo.Clear();
        }

        public void Dispose() => _map.Changing -= OnChanging;

        /// <summary>Puts every cell of the step back, and returns the step that would put them back again.</summary>
        Step Swap(Step step)
        {
            var width = _map.Grid.Width;
            var back = new Step { Label = step.Label };
            _replaying = true;
            try
            {
                // Newest first is not needed: each cell is recorded once, with its state before
                // the whole edit.
                for (var i = 0; i < step.Cells.Count; i++)
                {
                    var cell = step.Cells[i];
                    var x = cell % width;
                    var z = cell / width;
                    back.Cells.Add(cell);
                    back.States.Add(_map.Snapshot(x, z));
                    _map.Restore(x, z, step.States[i]);
                }
            }
            finally
            {
                _replaying = false;
            }

            return back;
        }

        void OnChanging(int x, int z)
        {
            if (_recording == null || _replaying)
                return;
            var cell = z * _map.Grid.Width + x;
            if (!_seen.Add(cell))
                return;
            _recording.Cells.Add(cell);
            _recording.States.Add(_map.Snapshot(x, z));
        }
    }
}
