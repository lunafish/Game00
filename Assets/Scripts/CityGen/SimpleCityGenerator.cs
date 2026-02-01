using System.Collections.Generic;
using UnityEngine;

namespace CityGen
{
    public class SimpleCityGenerator : MonoBehaviour
    {
        [Header("L-System Settings")]
        public string axiom = "X";
        public Rule[] rules;
        [Range(1, 6)]
        public int iterations = 3;

        [Header("Road Settings")]
        public float length = 5f;
        public float angle = 90f;
        public float angleVariance = 0f; 
        public float width = 1f;
        public float snapDistance = 1f; // Distance to snap/connect nodes
        public float minSegmentLength = 0.5f; // Shortest road allowed
        [Range(1, 10)]
        public int maxResolutionPasses = 5; // Max iterations for graph stabilization

        // Graph Data
        private List<Vector3> nodes = new List<Vector3>();
        private HashSet<RoadSegment> uniqueSegments = new HashSet<RoadSegment>();
        private List<List<int>> adjacency = new List<List<int>>();

        // Turtle State
        struct TurtleState
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        // Graph Structures
        struct RoadSegment : System.IEquatable<RoadSegment>
        {
            public int a;
            public int b;

            public RoadSegment(int a, int b)
            {
                if (a < b) { this.a = a; this.b = b; }
                else { this.a = b; this.b = a; }
            }

            public bool Equals(RoadSegment other)
            {
                return a == other.a && b == other.b;
            }

            public override int GetHashCode()
            {
                return System.HashCode.Combine(a, b);
            }
        }

        private void Reset()
        {
            axiom = "X";
            rules = new Rule[]
            {
                new Rule { input = 'X', outputs = "[+FX][-FX]F" },
                new Rule { input = 'F', outputs = "F" } 
            };
            iterations = 3;
            length = 5f;
            angle = 90f;
            angleVariance = 10f;
            width = 1f;
            snapDistance = 1f;
            minSegmentLength = 1f;
            maxResolutionPasses = 5;
        }

        [ContextMenu("Generate")]
        public void Generate()
        {
            // 1. Generate Logic String
            string sequence = LSystem.GenerateSentence(axiom, rules, iterations);
            Debug.Log($"Generating Planar Graph: {sequence.Length} instructions...");

            // 2. Initial Setup
            ClearGraph();

            // 3. Turtle Traversal (Initial Graph Build)
            BuildInitialGraph(sequence);

            // 4. Multi-pass Resolution Loop (Stabilization)
            ResolveGraphStability();

            Debug.Log($"Graph Finalized: {nodes.Count} nodes, {uniqueSegments.Count} edges.");
        }

        private void ClearGraph()
        {
            nodes.Clear();
            uniqueSegments.Clear();
            adjacency.Clear();
        }

        private void BuildInitialGraph(string sequence)
        {
            Stack<TurtleState> stack = new Stack<TurtleState>();
            Vector3 currentPos = transform.position;
            Quaternion currentRot = transform.rotation;
            int currentIndex = GetOrAddNode(currentPos);

            Random.InitState(System.DateTime.Now.Millisecond);

            foreach (char c in sequence)
            {
                switch (c)
                {
                    case 'F':
                        Vector3 nextPos = currentPos + (currentRot * Vector3.forward * length);
                        int nextIndex = GetOrAddNode(nextPos);
                        if (currentIndex != nextIndex)
                        {
                            AddSegmentIfValid(currentIndex, nextIndex);
                        }
                        currentPos = nextPos;
                        currentIndex = nextIndex;
                        break;
                    case 'f':
                        currentPos += (currentRot * Vector3.forward * length);
                        currentIndex = GetOrAddNode(currentPos);
                        break;
                    case '+':
                        currentRot *= Quaternion.Euler(0, angle + Random.Range(-angleVariance, angleVariance), 0);
                        break;
                    case '-':
                        currentRot *= Quaternion.Euler(0, -(angle + Random.Range(-angleVariance, angleVariance)), 0);
                        break;
                    case '[':
                        stack.Push(new TurtleState { position = currentPos, rotation = currentRot });
                        break;
                    case ']':
                        if (stack.Count > 0)
                        {
                            TurtleState popped = stack.Pop();
                            currentPos = popped.position;
                            currentRot = popped.rotation;
                            currentIndex = GetOrAddNode(currentPos);
                        }
                        break;
                }
            }
        }

        private void ResolveGraphStability()
        {
            bool changed = true;
            int pass = 0;
            while (changed && pass++ < maxResolutionPasses)
            {
                changed = false;
                
                // Pass A: Resolve intersections and T-junctions
                changed |= ResolveIntersections();
                
                // Pass B: Merge nearby nodes
                changed |= OptimizeGraph();
                
                // Pass C: Remove redundant short segments
                changed |= PruneShortSegments();

                if (changed) Debug.Log($"Resolution Pass {pass} applied changes...");
            }
        }

        private bool PruneShortSegments()
        {
            if (minSegmentLength <= 0) return false;

            float minSq = minSegmentLength * minSegmentLength;
            List<RoadSegment> toRemove = new List<RoadSegment>();

            foreach (var seg in uniqueSegments)
            {
                if (Vector3.SqrMagnitude(nodes[seg.a] - nodes[seg.b]) < minSq)
                {
                    toRemove.Add(seg);
                }
            }

            foreach (var seg in toRemove)
            {
                uniqueSegments.Remove(seg);
            }

            return toRemove.Count > 0;
        }

        private bool ResolveIntersections()
        {
            bool anyChange = false;
            bool localChange = true;
            int safety = 0;

            // Iteratively split until no more intersections found in this pass
            while (localChange && safety++ < 10)
            {
                localChange = SplitCrossingSegments();
                localChange |= SnapEndpointsToSegments();
                if (localChange) anyChange = true;
            }
            return anyChange;
        }

        private bool SplitCrossingSegments()
        {
            List<RoadSegment> segList = new List<RoadSegment>(uniqueSegments);
            int count = segList.Count;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    RoadSegment s1 = segList[i];
                    RoadSegment s2 = segList[j];

                    if (s1.a == s2.a || s1.a == s2.b || s1.b == s2.a || s1.b == s2.b) continue;

                    Vector3 p1 = nodes[s1.a];
                    Vector3 p2 = nodes[s1.b];
                    Vector3 p3 = nodes[s2.a];
                    Vector3 p4 = nodes[s2.b];

                    if (GetLineIntersection(p1, p2, p3, p4, out Vector3 intersection))
                    {
                        // Split both
                        int newNodeIndex = GetOrAddNode(intersection);
                        
                        uniqueSegments.Remove(s1);
                        uniqueSegments.Remove(s2);

                        AddSegmentIfValid(s1.a, newNodeIndex);
                        AddSegmentIfValid(newNodeIndex, s1.b);
                        AddSegmentIfValid(s2.a, newNodeIndex);
                        AddSegmentIfValid(newNodeIndex, s2.b);
                        
                        return true; // Restart
                    }
                }
            }
            return false;
        }

        private bool SnapEndpointsToSegments()
        {
            // Check if any node lies on a segment it doesn't belong to
            List<RoadSegment> segList = new List<RoadSegment>(uniqueSegments);
            
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 p = nodes[i];
                foreach (var seg in segList)
                {
                    if (seg.a == i || seg.b == i) continue; // It's strictly one of the endpoints

                    Vector3 p1 = nodes[seg.a];
                    Vector3 p2 = nodes[seg.b];

                    if (IsPointOnSegment(p, p1, p2, 0.1f)) // Small tolerance for "on line"
                    {
                        // Split segment 'seg' at node 'i'
                        uniqueSegments.Remove(seg);
                        AddSegmentIfValid(seg.a, i);
                        AddSegmentIfValid(i, seg.b);
                        return true; // Restart
                    }
                }
            }
            return false;
        }

        bool IsPointOnSegment(Vector3 point, Vector3 start, Vector3 end, float tolerance)
        {
            float length = Vector3.Distance(start, end);
            float d1 = Vector3.Distance(point, start);
            float d2 = Vector3.Distance(point, end);

            // Check if point is roughly on the line and between endpoints
            if (d1 + d2 >= length - tolerance && d1 + d2 <= length + tolerance)
            {
                // Refined check: Perpendicular distance using vector projection
                // Only works if length > 0
                if (length < 0.0001f) return false;

                Vector3 direction = (end - start).normalized;
                Vector3 projectedPoint = start + Vector3.Dot(point - start, direction) * direction;
                
                if (Vector3.Distance(point, projectedPoint) < tolerance)
                {
                    // Ensure strictly inside (not endpoints)
                    if (d1 > tolerance && d2 > tolerance) return true;
                }
            }
            return false;
        }

        void AddSegmentIfValid(int a, int b)
        {
            if (a == b) return;
            uniqueSegments.Add(new RoadSegment(a, b));
        }

        bool GetLineIntersection(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, out Vector3 result)
        {
            result = Vector3.zero;
            
            float x1 = p1.x; float z1 = p1.z;
            float x2 = p2.x; float z2 = p2.z;
            float x3 = p3.x; float z3 = p3.z;
            float x4 = p4.x; float z4 = p4.z;

            float denom = (z4 - z3) * (x2 - x1) - (x4 - x3) * (z2 - z1);
            if (Mathf.Abs(denom) < 0.0001f) return false;

            float ua = ((x4 - x3) * (z1 - z3) - (z4 - z3) * (x1 - x3)) / denom;
            float ub = ((x2 - x1) * (z1 - z3) - (z2 - z1) * (x1 - x3)) / denom;

            float tolerance = 0.001f;
            if (ua >= tolerance && ua <= 1f - tolerance && ub >= tolerance && ub <= 1f - tolerance)
            {
                result = new Vector3(x1 + ua * (x2 - x1), 0, z1 + ua * (z2 - z1));
                return true;
            }

            return false;
        }

        private bool OptimizeGraph()
        {
            bool nodesChanged = false;
            int[] nodeMap = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) nodeMap[i] = i;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodeMap[i] != i) continue;
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    if (nodeMap[j] != j) continue;
                    if (Vector3.SqrMagnitude(nodes[i] - nodes[j]) <= snapDistance * snapDistance)
                    {
                        nodeMap[j] = i;
                        nodesChanged = true;
                    }
                }
            }

            bool segmentsChanged = false;
            HashSet<RoadSegment> optimizedSegments = new HashSet<RoadSegment>();
            foreach (var seg in uniqueSegments)
            {
                int newA = nodeMap[seg.a];
                int newB = nodeMap[seg.b];
                if (newA != newB)
                {
                    if (optimizedSegments.Add(new RoadSegment(newA, newB)))
                    {
                        if (newA != seg.a || newB != seg.b) segmentsChanged = true;
                    }
                    else
                    {
                        // Duplicate segment removed
                        segmentsChanged = true;
                    }
                }
                else
                {
                    // Self-loop removed
                    segmentsChanged = true;
                }
            }
            uniqueSegments = optimizedSegments;

            // 3. Consolidate Node List (Only if needed, but for stability we always re-squeeze if something changed)
            if (nodesChanged || segmentsChanged)
            {
                List<Vector3> squeezedNodes = new List<Vector3>();
                Dictionary<int, int> oldToNew = new Dictionary<int, int>();

                foreach (var seg in uniqueSegments)
                {
                    if (!oldToNew.ContainsKey(seg.a))
                    {
                        oldToNew[seg.a] = squeezedNodes.Count;
                        squeezedNodes.Add(nodes[seg.a]);
                    }
                    if (!oldToNew.ContainsKey(seg.b))
                    {
                        oldToNew[seg.b] = squeezedNodes.Count;
                        squeezedNodes.Add(nodes[seg.b]);
                    }
                }

                nodes = squeezedNodes;

                HashSet<RoadSegment> consolidatedSegments = new HashSet<RoadSegment>();
                foreach (var seg in uniqueSegments)
                {
                    consolidatedSegments.Add(new RoadSegment(oldToNew[seg.a], oldToNew[seg.b]));
                }
                uniqueSegments = consolidatedSegments;
            }

            // 5. Rebuild Adjacency
            List<List<int>> newAdjacency = new List<List<int>>();
            for (int i = 0; i < nodes.Count; i++) newAdjacency.Add(new List<int>());

            foreach (var seg in uniqueSegments)
            {
                newAdjacency[seg.a].Add(seg.b);
                newAdjacency[seg.b].Add(seg.a);
            }
            adjacency = newAdjacency;

            return nodesChanged || segmentsChanged;
        }
        // Helper to find existing node or add new one
        int GetOrAddNode(Vector3 pos)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Vector3.SqrMagnitude(nodes[i] - pos) < 0.0001f)
                {
                    return i;
                }
            }
            nodes.Add(pos);
            adjacency.Add(new List<int>());
            return nodes.Count - 1;
        }

        void OnDrawGizmos()
        {
            if (nodes == null || uniqueSegments == null) return;

            // 1. Draw Edges
            Gizmos.color = new Color(0, 1, 1, 0.5f); // Semi-transparent Cyan
            foreach (var seg in uniqueSegments)
            {
                if (seg.a < nodes.Count && seg.b < nodes.Count)
                {
                    Gizmos.DrawLine(nodes[seg.a], nodes[seg.b]);
                }
            }

            // 2. Draw Nodes (Connectivity Visualization)
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i >= adjacency.Count) continue;

                int degree = adjacency[i].Count;
                if (degree == 0) continue; // Skip truly isolated nodes

                if (degree == 1) Gizmos.color = Color.red;           // Dead End
                else if (degree == 2) Gizmos.color = Color.yellow;   // Road/Path
                else Gizmos.color = Color.green;                     // Intersection

                Gizmos.DrawWireSphere(nodes[i], 0.25f);
            }
        }
    }
}
