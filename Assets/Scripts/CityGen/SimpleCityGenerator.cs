using System.Collections.Generic;
using UnityEngine;

namespace CityGen
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SimpleCityGenerator : MonoBehaviour
    {
        [Header("Subdivision Settings")]
        public int seed = 1234;
        public Vector2 citySize = new Vector2(100, 100);
        public float minBlockSize = 20f;
        [Range(0, 0.4f)] public float splitJitter = 0.1f;
        [Range(0, 5f)] public float nodeJitter = 1.0f;
        public int maxDepth = 5;

        [Header("Mesh Settings")]
        public float roadWidth = 2.0f;
        [Range(0.001f, 2.0f)] public float weldTolerance = 2.0f;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;

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
            SetupComponents();
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

            // 4. Mesh Generation
            GenerateMesh();

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

        private void GenerateMesh()
        {
            if (meshFilter == null) return;

            // --- PASS 1: Roads Only ---
            List<Vector3> roadVerts = new List<Vector3>();
            List<Color> roadColors = new List<Color>();
            List<int> roadTris = new List<int>();

            foreach (var seg in uniqueSegments)
            {
                GenerateRoadMesh(seg, roadVerts, roadColors, roadTris);
            }

            Mesh roadMesh = new Mesh();
            roadMesh.vertices = roadVerts.ToArray();
            roadMesh.colors = roadColors.ToArray();
            roadMesh.SetTriangles(roadTris.ToArray(), 0);

            // Weld roads first to get stable intersection points
            WeldVertices(roadMesh, false); 
            
            // Get the welded road data
            List<Vector3> finalVerts = new List<Vector3>(roadMesh.vertices);
            List<Color> finalColors = new List<Color>(roadMesh.colors);
            List<int> finalTris = new List<int>(roadMesh.GetTriangles(0));

            // --- PASS 2: Junctions Snapped to Welded Roads ---
            int roadVertexCount = finalVerts.Count;
            for (int i = 0; i < nodes.Count; i++)
            {
                GenerateJunctionMeshSnapped(i, finalVerts, finalColors, finalTris, roadVertexCount);
            }

            // Create Final Mesh
            Mesh mesh = new Mesh();
            mesh.name = "CityNetwork_SnappedJunctions";
            mesh.vertices = finalVerts.ToArray();
            mesh.colors = finalColors.ToArray();
            mesh.SetTriangles(finalTris.ToArray(), 0);

            // Final welding pass to connect junctions to roads
            WeldVertices(mesh, true);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshFilter.mesh = mesh;
        }

        private void SetupComponents()
        {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();

            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            // Ensure we have a proper URP Lit material for the road mesh
            if (meshRenderer.sharedMaterials == null || meshRenderer.sharedMaterials.Length < 1 || meshRenderer.sharedMaterials[0] == null)
            {
                Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
                if (urpLit == null) urpLit = Shader.Find("Standard");

                Material m = new Material(urpLit);
                m.name = "Road_URP_Lit";
                m.color = new Color(0.2f, 0.2f, 0.2f); // Dark road surface
                
                // URP Lit properties (if using URP)
                if (urpLit.name.Contains("Universal Render Pipeline"))
                {
                    m.SetFloat("_Roughness", 0.8f);
                    m.SetFloat("_Metallic", 0.0f);
                }
                
                meshRenderer.sharedMaterials = new Material[] { m };
            }
        }

        private void GenerateRoadMesh(RoadSegment seg, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            Vector3 pA = nodes[seg.a];
            Vector3 pB = nodes[seg.b];
            float dist = Vector3.Distance(pA, pB);
            
            if (dist < roadWidth * 1.1f) return;

            Vector3 dir = (pB - pA) / dist;
            Vector3 side = new Vector3(-dir.z, 0, dir.x) * (roadWidth / 2f);
            
            // Re-applying offsets to road ends
            float offset = roadWidth / 2f;
            Vector3 startP = pA + dir * offset;
            Vector3 endP = pB - dir * offset;

            Color roadColor = Color.white;
            int startIdx = verts.Count;
            
            verts.Add(startP - side); colors.Add(roadColor); // 0
            verts.Add(startP + side); colors.Add(roadColor); // 1
            verts.Add(endP + side);   colors.Add(roadColor); // 2
            verts.Add(endP - side);   colors.Add(roadColor); // 3

            tris.Add(startIdx + 0); tris.Add(startIdx + 1); tris.Add(startIdx + 2);
            tris.Add(startIdx + 0); tris.Add(startIdx + 2); tris.Add(startIdx + 3);
        }

        private void GenerateJunctionMeshSnapped(int nodeIdx, List<Vector3> verts, List<Color> colors, List<int> tris, int roadVertexCount)
        {
            if (adjacency[nodeIdx].Count == 0) return;

            Vector3 center = nodes[nodeIdx];
            float hw = roadWidth / 2f;
            Color col = Color.white;
            int startIdx = verts.Count;

            // 1. Define base square corners
            Vector3[] baseCorners = new Vector3[]
            {
                center + new Vector3(-hw, 0, -hw), // BL
                center + new Vector3(-hw, 0,  hw), // TL
                center + new Vector3( hw, 0,  hw), // TR
                center + new Vector3( hw, 0, -hw)  // BR
            };

            // 2. Snap corners to nearest road vertices
            // We search through the existing road vertices (Pass 1)
            for (int i = 0; i < 4; i++)
            {
                Vector3 snappedPos = baseCorners[i];
                float minDist = roadWidth * 0.4f; // Allow snapping up to 40% of road width
                
                for (int vIdx = 0; vIdx < roadVertexCount; vIdx++) 
                {
                    float d = Vector3.Distance(baseCorners[i], verts[vIdx]);
                    if (d < minDist)
                    {
                        minDist = d;
                        snappedPos = verts[vIdx];
                    }
                }
                verts.Add(snappedPos);
                colors.Add(col);
            }

            // CW Winding
            tris.Add(startIdx + 0); tris.Add(startIdx + 1); tris.Add(startIdx + 2);
            tris.Add(startIdx + 0); tris.Add(startIdx + 2); tris.Add(startIdx + 3);
        }

        private void WeldVertices(Mesh mesh, bool logResult = true)
        {
            Vector3[] oldVerts = mesh.vertices;
            Color[] oldColors = mesh.colors;
            List<Vector3> newVerts = new List<Vector3>();
            List<Color> newColors = new List<Color>();
            
            int submeshCount = mesh.subMeshCount;
            List<int[]> newSubmeshTris = new List<int[]>();
            
            // Scaled quantization to allow stable floating point comparison
            float invTol = 1f / Mathf.Max(0.001f, weldTolerance);
            Dictionary<Vector3Int, int> weldMap = new Dictionary<Vector3Int, int>();

            for (int subIdx = 0; subIdx < submeshCount; subIdx++)
            {
                int[] oldTris = mesh.GetTriangles(subIdx);
                int[] newTris = new int[oldTris.Length];

                for (int i = 0; i < oldTris.Length; i++)
                {
                    int oldVIdx = oldTris[i];
                    Vector3 pos = oldVerts[oldVIdx];
                    
                    Vector3Int qPos = new Vector3Int(
                        Mathf.RoundToInt(pos.x * invTol),
                        Mathf.RoundToInt(pos.y * invTol),
                        Mathf.RoundToInt(pos.z * invTol)
                    );

                    if (!weldMap.TryGetValue(qPos, out int newVIdx))
                    {
                        newVIdx = newVerts.Count;
                        newVerts.Add(pos);
                        newColors.Add(oldColors.Length > oldVIdx ? oldColors[oldVIdx] : Color.white);
                        weldMap.Add(qPos, newVIdx);
                    }
                    newTris[i] = newVIdx;
                }
                newSubmeshTris.Add(newTris);
            }

            mesh.Clear();
            mesh.vertices = newVerts.ToArray();
            mesh.colors = newColors.ToArray();
            mesh.subMeshCount = submeshCount;
            for (int i = 0; i < submeshCount; i++)
            {
                mesh.SetTriangles(newSubmeshTris[i], i);
            }

            if (logResult)
            {
                int originalCount = oldVerts.Length;
                int weldedCount = newVerts.Count;
                float reduction = originalCount > 0 ? (1f - (float)weldedCount / originalCount) * 100f : 0f;
                Debug.Log($"[Mesh Optimization] Welding complete: {originalCount} -> {weldedCount} vertices ({reduction:F1}% reduction)");
            }
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
