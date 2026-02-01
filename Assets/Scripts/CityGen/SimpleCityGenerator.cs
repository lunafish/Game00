using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics;

namespace CityGen
{
    /// <summary>
    /// 재귀적 분할(BSP) 알고리즘을 사용하여 도시 도로망을 생성하고 메쉬를 형성하는 클래스입니다.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SimpleCityGenerator : MonoBehaviour
    {
        [Header("분할 설정 (Subdivision Settings)")]
        public int seed = 1234;
        public Vector2 citySize = new Vector2(100, 100);
        public float minBlockSize = 20f;
        [Range(0, 0.4f)] public float splitJitter = 0.1f;
        [Range(0, 5f)] public float nodeJitter = 1.0f;
        public int maxDepth = 5;

        [Header("메쉬 설정 (Mesh Settings)")]
        public float roadWidth = 2.0f;
        [Range(0.001f, 2.0f)] public float weldTolerance = 2.0f;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;

        // 그래프 데이터
        protected List<Vector3> nodes = new List<Vector3>();
        protected HashSet<RoadSegment> uniqueSegments = new HashSet<RoadSegment>();
        protected List<List<int>> adjacency = new List<List<int>>();

        // 최적화 데이터: 노드 인덱스별로 생성된 도로 정점들을 저장 (스냅 성능 향상용)
        private Dictionary<int, List<Vector3>> nodeToRoadVertices = new Dictionary<int, List<Vector3>>();

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

        /// <summary>
        /// 도시 생성 프로세스를 시작합니다.
        /// </summary>
        [ContextMenu("Generate")]
        public void Generate()
        {
            Stopwatch sw = Stopwatch.StartNew();
            
            SetupComponents();
            ClearGraph();
            UnityEngine.Random.InitState(seed);

            Rect totalArea = new Rect(-citySize.x / 2f, -citySize.y / 2f, citySize.x, citySize.y);
            
            // 1. 외곽 경계 생성
            DrawBoundary(totalArea);

            // 2. 재귀적 공간 분할
            Subdivide(totalArea, 0);

            // 3. 노드 지터(Jitter) 적용 (자연스러운 그리드 형성)
            JitterNodes();

            // 4. 인접 리스트 재구축
            RebuildAdjacency();

            // 5. 메쉬 생성 및 최적화
            GenerateMesh();

            sw.Stop();
            UnityEngine.Debug.Log($"도시 생성 완료: {nodes.Count} 노드, {uniqueSegments.Count} 도로 구간 (소요 시간: {sw.ElapsedMilliseconds}ms)");
        }

        /// <summary>
        /// 노드들에 무작위 오프셋을 주어 격자무늬를 자연스럽게 흐트러뜨립니다.
        /// </summary>
        private void JitterNodes()
        {
            if (nodeJitter <= 0) return;

            float eps = 0.1f;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 p = nodes[i];
                
                // 외곽 경계 노드는 이동시키지 않음
                bool onBoundaryX = Mathf.Abs(p.x - (-citySize.x / 2f)) < eps || Mathf.Abs(p.x - (citySize.x / 2f)) < eps;
                bool onBoundaryZ = Mathf.Abs(p.z - (-citySize.y / 2f)) < eps || Mathf.Abs(p.z - (citySize.y / 2f)) < eps;
                if (onBoundaryX || onBoundaryZ) continue;

                // 인접 노드로부터의 척력 계산 (뭉침 방지)
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
                    repulsionDir = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0, UnityEngine.Random.Range(-1f, 1f)).normalized;
                }

                float individualJitter = UnityEngine.Random.Range(nodeJitter * 0.5f, nodeJitter);
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

        /// <summary>
        /// 주어진 영역을 가로 또는 세로로 랜덤하게 분할합니다.
        /// </summary>
        private void Subdivide(Rect area, int depth)
        {
            if (depth >= maxDepth) return;
            if (area.width < minBlockSize * 2f && area.height < minBlockSize * 2f) return;

            bool horizontalSplit = area.width < area.height;
            if (area.width > area.height * 1.5f) horizontalSplit = false;
            else if (area.height > area.width * 1.5f) horizontalSplit = true;

            float splitPos = horizontalSplit 
                ? area.yMin + area.height * UnityEngine.Random.Range(0.4f - splitJitter, 0.6f + splitJitter)
                : area.xMin + area.width * UnityEngine.Random.Range(0.4f - splitJitter, 0.6f + splitJitter);

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

        /// <summary>
        /// 2단계 프로세스로 메쉬를 생성합니다: 1. 도로 생성 및 병합, 2. 교차로 스냅 및 생성.
        /// </summary>
        private void GenerateMesh()
        {
            if (meshFilter == null) return;
            nodeToRoadVertices.Clear();

            // --- Pass 1: 도로 구간만 생성 ---
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

            // 1차 병합: 도로 끝단 정점들을 노드 위치로 consolidated
            WeldVertices(roadMesh, false); 
            
            List<Vector3> finalVerts = new List<Vector3>(roadMesh.vertices);
            List<Color> finalColors = new List<Color>(roadMesh.colors);
            List<int> finalTris = new List<int>(roadMesh.GetTriangles(0));

            // 최적화: 병합된 도로 정점들을 공간 맵에 재배치
            RebuildNodeToVertexMap(finalVerts);

            // --- Pass 2: 교차로를 도로 끝단 정점에 맞추어 생성 ---
            for (int i = 0; i < nodes.Count; i++)
            {
                GenerateJunctionMeshSnapped(i, finalVerts, finalColors, finalTris);
            }

            // 최종 메쉬 조립
            Mesh mesh = new Mesh();
            mesh.name = "CityNetwork_Optimized";
            mesh.vertices = finalVerts.ToArray();
            mesh.colors = finalColors.ToArray();
            mesh.SetTriangles(finalTris.ToArray(), 0);

            // 최종 병합: 교차로와 도로를 하나의 연속된 데이터로 통합
            WeldVertices(mesh, true);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshFilter.mesh = mesh;
        }

        /// <summary>
        /// 병합된 도로 정점들을 각 노드 인덱스별로 분류하여 검색 성능을 최적화합니다.
        /// </summary>
        private void RebuildNodeToVertexMap(List<Vector3> weldedVerts)
        {
            nodeToRoadVertices.Clear();
            float snapDist = roadWidth * 0.6f;
            float snapDistSq = snapDist * snapDist;

            for (int vIdx = 0; vIdx < weldedVerts.Count; vIdx++)
            {
                Vector3 v = weldedVerts[vIdx];
                for (int nIdx = 0; nIdx < nodes.Count; nIdx++)
                {
                    if (Vector3.SqrMagnitude(v - nodes[nIdx]) < snapDistSq)
                    {
                        if (!nodeToRoadVertices.ContainsKey(nIdx))
                            nodeToRoadVertices[nIdx] = new List<Vector3>();
                        nodeToRoadVertices[nIdx].Add(v);
                    }
                }
            }
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
                m.name = "Road_Material";
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

        /// <summary>
        /// 도로 구간을 생성합니다. 각 끝단은 노드 중심에서 roadWidth/2만큼 오프셋됩니다.
        /// </summary>
        private void GenerateRoadMesh(RoadSegment seg, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            Vector3 pA = nodes[seg.a];
            Vector3 pB = nodes[seg.b];
            float dist = Vector3.Distance(pA, pB);
            
            if (dist < roadWidth * 1.1f) return;

            Vector3 dir = (pB - pA) / dist;
            Vector3 side = new Vector3(-dir.z, 0, dir.x) * (roadWidth / 2f);
            
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

        /// <summary>
        /// 교차로 사각형을 생성하고, 인접한 도로 정점들에 위치를 맞춥니다(Snap).
        /// </summary>
        private void GenerateJunctionMeshSnapped(int nodeIdx, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            if (adjacency[nodeIdx].Count == 0) return;

            Vector3 center = nodes[nodeIdx];
            float hw = roadWidth / 2f;
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
            // 최적화: 이 노드에 근접한 도로 정점들 중에서만 스냅 대상을 검색
            List<Vector3> candidateRoadVerts = nodeToRoadVertices.ContainsKey(nodeIdx) 
                ? nodeToRoadVertices[nodeIdx] 
                : new List<Vector3>();
            
            for (int i = 0; i < 4; i++)
            {
                Vector3 snappedPos = baseCorners[i];
                float minDist = roadWidth * 0.4f; // Allow snapping up to 40% of road width
                
                foreach (var rv in candidateRoadVerts) 
                {
                    float d = Vector3.Distance(baseCorners[i], rv);
                    if (d < minDist)
                    {
                        minDist = d;
                        snappedPos = rv;
                    }
                }
                verts.Add(snappedPos);
                colors.Add(Color.white);
            }

            // CW Winding
            tris.Add(startIdx + 0); tris.Add(startIdx + 1); tris.Add(startIdx + 2);
            tris.Add(startIdx + 0); tris.Add(startIdx + 2); tris.Add(startIdx + 3);
        }

        /// <summary>
        /// 격자 공간 분할을 사용하여 지정된 거리(Tolerance) 내의 중복 정점들을 병합합니다.
        /// </summary>
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
                UnityEngine.Debug.Log($"[메쉬 최적화] 병합 완료: 정점 수 {originalCount} -> {weldedCount} ({reduction:F1}% 감소)");
            }
        }

        protected void ClearGraph()
        {
            nodes.Clear();
            uniqueSegments.Clear();
            adjacency.Clear();
            nodeToRoadVertices.Clear();
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
