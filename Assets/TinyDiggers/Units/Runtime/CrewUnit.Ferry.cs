using System;
using UnityEngine;

namespace TinyDiggers.Units
{
    /// <summary>
    /// Getting aboard the landing craft, riding it and landing from it (FERRY_PROPOSAL.md, slice C).
    ///
    /// Boarding: the unit drives to a cell inland of the ramp foot, lines up and reverses straight
    /// down to the foot, the way the dump truck backs in to tip, then goes on up the ramp into its
    /// lane still tail first, so it faces the ramp and can drive off forwards (the forge's loading
    /// scene). Aboard it holds no cell and does nothing but follow its lane. When the craft has its
    /// ramp down on another beach it drives forward off it and a few cells inland, and holds there
    /// for orders (Ronan, 2026-09-24: the machines go to the craft, it carries them, it unloads
    /// them somewhere else).
    /// </summary>
    public sealed partial class CrewUnit
    {
        /// <summary>Seconds a machine takes to go up or down the ramp.</summary>
        public const float RampDriveSeconds = 1.5f;

        /// <summary>Cells inland of the ramp foot a machine stops after landing.</summary>
        public const int LandInlandCells = 4;

        Ferry _ferry;
        int _lane = -1;
        Vector2 _boardedAt;
        float _rampProgress;

        /// <summary>The landing craft this unit is boarding, riding or leaving; null otherwise.</summary>
        public Ferry Ferry => _ferry;

        /// <summary>Its lane on the craft, or -1.</summary>
        public int FerryLane => _lane;

        /// <summary>0 at the ramp foot, 1 parked in its lane: how far up the ramp, for the view.</summary>
        public float RampProgress => _rampProgress;

        /// <summary>Whether it is on the craft or its ramp, off the ground.</summary>
        public bool OnFerry => State == CrewUnitState.Boarding || State == CrewUnitState.Aboard || State == CrewUnitState.Landing;

        /// <summary>
        /// Sends the unit aboard <paramref name="ferry"/>, which must be beached with its ramp down
        /// and have a lane free. False, with the reason, when it cannot go.
        /// </summary>
        public bool OrderBoard(Ferry ferry, out string why)
        {
            if (ferry == null)
                throw new ArgumentNullException(nameof(ferry));
            if (OnFerry)
            {
                why = "already aboard";
                return false;
            }

            if (ferry.State != FerryState.RampDown)
            {
                why = "the landing craft's ramp is not down";
                return false;
            }

            if (!ferry.TryReserveLane(Id, out var lane))
            {
                why = "the landing craft is full";
                return false;
            }

            var foot = ferry.Landing.RampFoot;
            var lineUp = ferry.LineUpCell(ReverseCells);
            var me = Cell;
            _dispatcher.Release(Id);
            ClearPath();
            if (!_dispatcher.Regions.CanReach(me.x, me.y, lineUp.x, lineUp.y)
                || !_pathfinder.TryFindPath(me.x, me.y, lineUp.x, lineUp.y, _path))
            {
                ferry.Leave(Id);
                why = "no way to the landing craft's ramp";
                return false;
            }

            // Then straight back down the run to the ramp foot, tail first.
            _reverseFrom = _path.Count;
            var back = new Vector2Int(Math.Sign(foot.x - lineUp.x), Math.Sign(foot.y - lineUp.y));
            for (var k = 1; k <= ReverseCells; k++)
                _path.Add(lineUp + back * k);
            _backsInStraight = true;

            _ferry = ferry;
            _lane = lane;
            _ordered = false;
            Holding = true;
            Job = CrewJobKind.Board;
            JobTarget = JobStand = foot;
            _pathIndex = 0;
            _rethink = false;
            _repath = false;
            MarkPath();
            SetState(CrewUnitState.Moving, "Boarding the landing craft");
            why = null;
            return true;
        }

        /// <summary>At the ramp foot, tail to the craft: off the ground and up into the lane.</summary>
        void StartUpTheRamp()
        {
            _dispatcher.LeaveGround(Id);
            _rampProgress = 0f;
            Heading = _ferry.Heading;
            Reversing = true;
            SetState(CrewUnitState.Boarding, "Reversing up the ramp");
        }

        /// <summary>Steps a unit that is on the craft or its ramp; false for any other.</summary>
        bool FerryStep(float deltaTime)
        {
            if (!OnFerry || _ferry == null)
                return false;

            var foot = new Vector2(_ferry.Landing.RampFoot.x + 0.5f, _ferry.Landing.RampFoot.y + 0.5f);
            var lane = _ferry.LanePosition(_lane);
            Heading = _ferry.Heading;
            switch (State)
            {
                case CrewUnitState.Boarding:
                    _rampProgress = Mathf.Min(1f, _rampProgress + deltaTime / RampDriveSeconds);
                    Position = Vector2.Lerp(foot, lane, _rampProgress);
                    if (_rampProgress >= 1f)
                    {
                        Reversing = false;
                        _boardedAt = _ferry.Landing.Hull;
                        _ferry.MarkAboard(Id, true);
                        Job = CrewJobKind.None;
                        SetState(CrewUnitState.Aboard, "Aboard the landing craft");
                    }

                    break;

                case CrewUnitState.Aboard:
                    Position = lane;
                    // Ramp down on a new beach: time to go ashore.
                    if (_ferry.State == FerryState.RampDown && (_ferry.Landing.Hull - _boardedAt).sqrMagnitude > 1f)
                    {
                        _ferry.MarkAboard(Id, false);
                        SetState(CrewUnitState.Landing, "Driving off the landing craft");
                    }

                    break;

                case CrewUnitState.Landing:
                    _rampProgress = Mathf.Max(0f, _rampProgress - deltaTime / RampDriveSeconds);
                    Position = Vector2.Lerp(foot, lane, _rampProgress);
                    if (_rampProgress <= 0f)
                        GoAshore();
                    break;
            }

            return true;
        }

        /// <summary>Off the ramp and onto the beach: back on the ground, and a few cells inland.</summary>
        void GoAshore()
        {
            var foot = _ferry.Landing.RampFoot;
            var inland = _ferry.LineUpCell(LandInlandCells + _lane);
            _ferry.Leave(Id);
            _ferry = null;
            _lane = -1;
            Position = new Vector2(foot.x + 0.5f, foot.y + 0.5f);
            _dispatcher.SetCell(Id, foot.x, foot.y);
            SetState(CrewUnitState.Idle, "Landed");
            if (!OrderMoveTo(inland.x, inland.y))
                Hold();
        }
    }
}
