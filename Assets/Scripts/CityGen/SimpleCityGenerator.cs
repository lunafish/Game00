using System.Collections.Generic;
using UnityEngine;

namespace CityGen
{
    public class SimpleCityGenerator : MonoBehaviour
    {
        [Header("Subdivision Settings")]
        public int seed = 1234;
        public Vector2 citySize = new Vector2(100, 100);
        public float minBlockSize = 20f;
        [Range(0, 0.4f)] public float splitJitter = 0.1f;
        [Range(0, 5f)] public float nodeJitter = 1.0f;
        public int maxDepth = 5;

        // Graph Data
        protected List<Vector3> nodes = new List<Vector3>();
        protected HashSet<RoadSegment> uniqueSegments = new HashSet<RoadSegment>();
        protected List<List<int>> adjacency = new List<List<int>>();

        [System.Serializable]
        public struct RoadSegment : System.IEquatable<RoadSegment>
        {
            public int a, b;
            public RoadSegment(int a, int b)
            {
                if (a < b) { this.a = a; this.b = b; }
                else { this.a = b; this.b = a; }
            }
            public bool Equals(RoadSegment other) => a == other.a && b == other.b;
            public override int GetHashCode() => System.HashCode.Combine(a, b);
        }

        [ContextMenu("Generate")]
        public void Generate()
        {
            ClearGraph();
            Random.InitState(seed);

            Rect totalArea = new Rect(-citySize.x / 2f, -citySize.y / 2f, citySize.x, citySize.y);
            
            // 1. Draw Outer Boundary
            DrawBoundary(totalArea);

            // 2. Recursively Subdivide
            Subdivide(totalArea, 0);

            // 3. Post-processing: Node Jitter for natural look
            JitterNodes();

            // Final Adjacency Rebuild (since we might have split segments)
            RebuildAdjacency();

            Debug.Log($"Subdivision complete: {nodes.Count} nodes, {uniqueSegments.Count} segments.");
        }

        private void JitterNodes()
        {
            if (nodeJitter <= 0) return;

            float eps = 0.1f;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 p = nodes[i];
                
                // Skip nodes on the outer boundary
                bool onBoundaryX = Mathf.Abs(p.x - (-citySize.x / 2f)) < eps || Mathf.Abs(p.x - (citySize.x / 2f)) < eps;
                bool onBoundaryZ = Mathf.Abs(p.z - (-citySize.y / 2f)) < eps || Mathf.Abs(p.z - (citySize.y / 2f)) < eps;
                if (onBoundaryX || onBoundaryZ) continue;

                // Calculate repulsion from neighbors
                Vector3 repulsionDir = Vector3.zero;
                List<int> neighbors = adjacency[i];
                
                if (neighbors.Count > 0)
                {
                    foreach (int nIdx in neighbors)
                    {
                        Vector3 toCurrent = nodes[i] - nodes[nIdx];
                        // Inverse square distance repulsion or simple directional bias
                        repulsionDir += toCurrent.normalized;
                    }
                    repulsionDir.Normalize();
                }

                // Apply jitter biased away from neighbors
                // If repulsion is zero (balanced grid), we use a random direction
                if (repulsionDir.sqrMagnitude < 0.01f)
                {
                    repulsionDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                }

                float individualJitter = Random.Range(nodeJitter * 0.5f, nodeJitter);
                nodes[i] += repulsionDir * individualJitter;
            }
        }

        private void RebuildAdjacency()
        {
            adjacency.Clear();
            for (int i = 0; i < nodes.Count; i++) adjacency.Add(new List<int>());
            foreach (var seg in uniqueSegments)
            {
                if (seg.a == seg.b) continue;
                adjacency[seg.a].Add(seg.b);
                adjacency[seg.b].Add(seg.a);
            }
        }

        private void DrawBoundary(Rect r)
        {
            Vector3 tl = new Vector3(r.xMin, 0, r.yMax);
            Vector3 tr = new Vector3(r.xMax, 0, r.yMax);
            Vector3 bl = new Vector3(r.xMin, 0, r.yMin);
            Vector3 br = new Vector3(r.xMax, 0, r.yMin);

            AddSplitLine(tl, tr);
            AddSplitLine(tr, br);
            AddSplitLine(br, bl);
            AddSplitLine(bl, tl);
        }

        private void Subdivide(Rect area, int depth)
        {
            if (depth >= maxDepth) return;
            if (area.width < minBlockSize * 2f && area.height < minBlockSize * 2f) return;

            bool horizontalSplit = area.width < area.height;
            if (area.width > area.height * 1.5f) horizontalSplit = false;
            else if (area.height > area.width * 1.5f) horizontalSplit = true;

            float splitPos = horizontalSplit 
                ? area.yMin + area.height * Random.Range(0.4f - splitJitter, 0.6f + splitJitter)
                : area.xMin + area.width * Random.Range(0.4f - splitJitter, 0.6f + splitJitter);

            if (horizontalSplit)
            {
                Vector3 start = new Vector3(area.xMin, 0, splitPos);
                Vector3 end = new Vector3(area.xMax, 0, splitPos);
                AddSplitLine(start, end);

                Subdivide(new Rect(area.x, area.y, area.width, splitPos - area.yMin), depth + 1);
                Subdivide(new Rect(area.x, splitPos, area.width, area.yMax - splitPos), depth + 1);
            }
            else
            {
                Vector3 start = new Vector3(splitPos, 0, area.yMin);
                Vector3 end = new Vector3(splitPos, 0, area.yMax);
                AddSplitLine(start, end);

                Subdivide(new Rect(area.x, area.y, splitPos - area.xMin, area.height), depth + 1);
                Subdivide(new Rect(splitPos, area.y, area.xMax - splitPos, area.height), depth + 1);
            }
        }

        private void AddSplitLine(Vector3 start, Vector3 end)
        {
            List<Vector3> splitPoints = new List<Vector3> { start, end };
            List<RoadSegment> existingSegments = new List<RoadSegment>(uniqueSegments);

            foreach (var seg in existingSegments)
            {
                Vector3 s1 = nodes[seg.a];
                Vector3 s2 = nodes[seg.b];

                // 1. Cross Intersections
                if (GetLineIntersection(start, end, s1, s2, out Vector3 inter))
                {
                    uniqueSegments.Remove(seg);
                    int interIdx = GetOrAddNode(inter);
                    uniqueSegments.Add(new RoadSegment(seg.a, interIdx));
                    uniqueSegments.Add(new RoadSegment(interIdx, seg.b));
                    if (!splitPoints.Contains(inter)) splitPoints.Add(inter);
                }

                // 2. T-Junctions (Start or End on an existing segment)
                if (IsPointNearSegment(start, s1, s2, out Vector3 snapStart))
                {
                    if (Vector3.Distance(start, snapStart) < 0.1f)
                    {
                        uniqueSegments.Remove(seg);
                        int startIdx = GetOrAddNode(start);
                        uniqueSegments.Add(new RoadSegment(seg.a, startIdx));
                        uniqueSegments.Add(new RoadSegment(startIdx, seg.b));
                        if (!splitPoints.Contains(start)) splitPoints.Add(start);
                    }
                }
                if (IsPointNearSegment(end, s1, s2, out Vector3 snapEnd))
                {
                    if (Vector3.Distance(end, snapEnd) < 0.1f)
                    {
                        uniqueSegments.Remove(seg);
                        int endIdx = GetOrAddNode(end);
                        uniqueSegments.Add(new RoadSegment(seg.a, endIdx));
                        uniqueSegments.Add(new RoadSegment(endIdx, seg.b));
                        if (!splitPoints.Contains(end)) splitPoints.Add(end);
                    }
                }
            }

            // Deduplicate and Sort
            splitPoints.Sort((a, b) => Vector3.Distance(start, a).CompareTo(Vector3.Distance(start, b)));
            
            for (int i = 0; i < splitPoints.Count - 1; i++)
            {
                if (Vector3.Distance(splitPoints[i], splitPoints[i+1]) > 0.1f)
                {
                    int a = GetOrAddNode(splitPoints[i]);
                    int b = GetOrAddNode(splitPoints[i+1]);
                    uniqueSegments.Add(new RoadSegment(a, b));
                }
            }
        }

        private bool GetLineIntersection(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, out Vector3 res)
        {
            res = Vector3.zero;
            float denom = (p4.z - p3.z) * (p2.x - p1.x) - (p4.x - p3.x) * (p2.z - p1.z);
            if (Mathf.Abs(denom) < 0.0001f) return false;
            float ua = ((p4.x - p3.x) * (p1.z - p3.z) - (p4.z - p3.z) * (p1.x - p3.x)) / denom;
            float ub = ((p2.x - p1.x) * (p1.z - p3.z) - (p2.z - p1.z) * (p1.x - p3.x)) / denom;
            if (ua > 0.01f && ua < 0.99f && ub > 0.01f && ub < 0.99f)
            {
                res = new Vector3(p1.x + ua * (p2.x - p1.x), 0, p1.z + ua * (p2.z - p1.z));
                return true;
            }
            return false;
        }

        private bool IsPointNearSegment(Vector3 p, Vector3 a, Vector3 b, out Vector3 closest)
        {
            closest = Vector3.zero;
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Vector3.SqrMagnitude(ab);
            if (t > 0.01f && t < 0.99f)
            {
                closest = a + t * ab;
                return true;
            }
            return false;
        }

        protected void ClearGraph()
        {
            nodes.Clear();
            uniqueSegments.Clear();
            adjacency.Clear();
        }

        protected int GetOrAddNode(Vector3 pos)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Vector3.SqrMagnitude(nodes[i] - pos) < 0.0001f) return i;
            }
            nodes.Add(pos);
            adjacency.Add(new List<int>());
            return nodes.Count - 1;
        }

        protected bool AddSegmentIfValid(int a, int b)
        {
            if (a == b) return false;
            RoadSegment seg = new RoadSegment(a, b);
            if (uniqueSegments.Add(seg))
            {
                while (adjacency.Count <= Mathf.Max(a, b)) adjacency.Add(new List<int>());
                adjacency[a].Add(b);
                adjacency[b].Add(a);
                return true;
            }
            return false;
        }

        protected virtual void OnDrawGizmos()
        {
            if (nodes == null || uniqueSegments == null) return;

            // Draw Roads
            Gizmos.color = new Color(0, 1, 1, 0.6f);
            foreach (var seg in uniqueSegments)
            {
                if (seg.a < nodes.Count && seg.b < nodes.Count)
                    Gizmos.DrawLine(nodes[seg.a], nodes[seg.b]);
            }

            // Draw Junctions
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i >= adjacency.Count) continue;
                int degree = adjacency[i].Count;
                if (degree == 0) continue;

                Gizmos.color = degree == 1 ? Color.red : (degree == 2 ? Color.yellow : Color.green);
                Gizmos.DrawWireSphere(nodes[i], 0.15f);
            }
        }
    }
}
