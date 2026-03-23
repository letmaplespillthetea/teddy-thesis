using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Represents one bone segment of the chordal skeleton.
    /// Start / End are 3D world positions (x, y from 2D spine, z from heightTable).
    /// </summary>
    public class SkeletonBone {

        public Vector3 Start  { get { return start;    } }
        public Vector3 End    { get { return end;      } }
        public List<SkeletonBone> Children { get { return children; } }

        readonly Vector3 start, end;
        readonly List<SkeletonBone> children = new List<SkeletonBone>();

        public SkeletonBone(Vector3 start, Vector3 end) {
            this.start = start;
            this.end   = end;
        }

        public void AddChild(SkeletonBone child) {
            children.Add(child);
        }

        /// <summary>Flatten the tree into a list of (start, end) bone pairs.</summary>
        public List<(Vector3, Vector3)> Flatten() {
            var result = new List<(Vector3, Vector3)>();
            FlattenRoutine(result);
            return result;
        }

        void FlattenRoutine(List<(Vector3, Vector3)> result) {
            result.Add((start, end));
            foreach (var child in children) {
                child.FlattenRoutine(result);
            }
        }
    }

}
