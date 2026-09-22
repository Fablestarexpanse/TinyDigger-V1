using NUnit.Framework;
using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction.Tests
{
    /// <summary>Slice 17 Part B: drawing and editing a road before it goes into the network.</summary>
    public class RoadDraftTests
    {
        static float Ground(Vector2 at) => 5f + at.x * 0.1f;

        [Test]
        public void NodesSnapTo45DegreesFromTheLastOneAndSitOnTheGround()
        {
            var draft = new RoadDraft();
            draft.Place(new Vector2(10f, 10f), Ground);
            var node = draft.Place(new Vector2(20f, 13f), Ground);

            Assert.That(node.Position.y, Is.EqualTo(10f).Within(1e-3f), "turned onto the nearest 45°: straight along x");
            Assert.That(node.Position.x, Is.EqualTo(10f + new Vector2(10f, 3f).magnitude).Within(1e-3f), "keeping its distance");
            Assert.That(node.Height, Is.EqualTo(Ground(node.Position)).Within(1e-4f));
            Assert.That(node.LockToGround, Is.True);

            draft.Snap45 = false;
            var free = draft.Place(new Vector2(25f, 17f), Ground);
            Assert.That(free.Position, Is.EqualTo(new Vector2(25f, 17f)));
        }

        [Test]
        public void ANodePlacedOnARoadJoinsIt()
        {
            var network = new RoadNetwork();
            network.AddRoad(new[] { new Vector3(0f, 5f, 0f), new Vector3(10f, 6f, 0f) }, 3);
            var end = network.NodeNear(new Vector2(10f, 0f), 0.1f);

            var draft = new RoadDraft { Snap45 = false };
            var node = draft.Place(new Vector2(10.8f, 0.5f), Ground, network);
            draft.Place(new Vector2(10f, 10f), Ground, network);

            Assert.That(node.Id, Is.EqualTo(end.Id), "it is the road's node");
            Assert.That(node.Position, Is.EqualTo(end.Position));
            Assert.That(node.Height, Is.EqualTo(6f), "at the road's height there");
            Assert.That(draft.Commit(network, 1f), Is.Not.Zero);
            Assert.That(network.Nodes.Count, Is.EqualTo(3));
            Assert.That(network.Degree(end.Id), Is.EqualTo(2));
        }

        [Test]
        public void RaisingANodeFreesItFromTheGroundAndMovingItKeepsItsHeight()
        {
            var draft = new RoadDraft { Snap45 = false };
            draft.Place(new Vector2(0f, 0f), Ground);
            draft.Place(new Vector2(10f, 0f), Ground);
            draft.Raise(1, 1f);

            Assert.That(draft.Nodes[1].LockToGround, Is.False);
            Assert.That(draft.Nodes[1].Height, Is.EqualTo(Ground(new Vector2(10f, 0f)) + 1f).Within(1e-4f));
            draft.Move(1, new Vector2(20f, 0f), Ground);
            Assert.That(draft.Nodes[1].Height, Is.EqualTo(7f).Within(1e-4f), "a freed node keeps its height when moved");

            draft.SetLocked(1, true, Ground);
            Assert.That(draft.Nodes[1].Height, Is.EqualTo(Ground(new Vector2(20f, 0f))).Within(1e-4f), "locked again, it drops to the ground");
        }

        [Test]
        public void ARoadOverTwiceTheMaxGradeCannotBeLaid()
        {
            var network = new RoadNetwork();
            var draft = new RoadDraft { Snap45 = false, MaxGrade = 0.12f };
            draft.Place(new Vector2(0f, 0f), _ => 5f);
            draft.Place(new Vector2(10f, 0f), _ => 5f);
            draft.Raise(1, 2f); // 20% on 1 m cells: steep, not refused
            Assert.That(draft.CanCommit(1f), Is.True);

            draft.Raise(1, 1f); // 30%: over twice 12%
            Assert.That(draft.CanCommit(1f), Is.False);
            Assert.That(draft.Commit(network, 1f), Is.Zero);
            Assert.That(network.Nodes, Is.Empty, "nothing went in");
        }

        [Test]
        public void EditingARoadMovesItsRealNodesAndKeepsItsId()
        {
            var network = new RoadNetwork();
            var road = network.AddRoad(new[] { new Vector3(0f, 5f, 0f), new Vector3(10f, 5f, 0f), new Vector3(20f, 5f, 0f) }, 3);
            var other = network.AddRoad(new[] { new Vector3(10f, 5f, 0f), new Vector3(10f, 5f, 10f) }, 3);
            var changed = new System.Collections.Generic.List<int>();
            network.Changed += changed.Add;

            var draft = new RoadDraft { Snap45 = false };
            draft.Load(network, road);
            draft.Move(1, new Vector2(10f, 3f), _ => 5f);
            Assert.That(draft.Commit(network, 1f), Is.EqualTo(road));

            var middle = network.NodeNear(new Vector2(10f, 3f), 0.1f);
            Assert.That(middle, Is.Not.Null);
            Assert.That(network.Degree(middle.Id), Is.EqualTo(3), "still the junction");
            Assert.That(changed, Does.Contain(road));
            Assert.That(changed, Does.Contain(other), "the road through the moved junction is re-planned too");
            Assert.That(network.Roads(), Is.EquivalentTo(new[] { road, other }));
        }
    }
}
