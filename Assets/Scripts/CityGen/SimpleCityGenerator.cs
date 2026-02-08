using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics;
using System.Linq;

namespace CityGen
{
    /// <summary>
    /// 재귀적 분할(BSP) 알고리즘을 사용하여 도시 도로망을 생성하고 메쉬를 형성하는 클래스입니다.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SimpleCityGenerator : MonoBehaviour, ISerializationCallbackReceiver
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

        // 그래프 데이터 (직렬화 가능하도록 속성 추가)
        [SerializeField, HideInInspector] protected List<Vector3> nodes = new List<Vector3>();
        protected HashSet<RoadSegment> uniqueSegments = new HashSet<RoadSegment>();
        protected List<List<int>> adjacency = new List<List<int>>();

        private Dictionary<int, Vector3[]> nodeJunctionCorners = new Dictionary<int, Vector3[]>();

        // 직렬화를 위한 임시 보관용 데이터
        [SerializeField, HideInInspector] private List<RoadSegment> serializedSegments = new List<RoadSegment>();
        [SerializeField, HideInInspector] private List<int> junctionKeys = new List<int>();
        
        [System.Serializable]
        public struct Vector3ArrayWrapper { public Vector3[] array; }
        [SerializeField, HideInInspector] private List<Vector3ArrayWrapper> junctionValues = new List<Vector3ArrayWrapper>();


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
            
            // 1. 초기 외곽 노드 생성
            int blNode = GetOrAddNode(new Vector3(totalArea.xMin, 0, totalArea.yMin));
            int brNode = GetOrAddNode(new Vector3(totalArea.xMax, 0, totalArea.yMin));
            int trNode = GetOrAddNode(new Vector3(totalArea.xMax, 0, totalArea.yMax));
            int tlNode = GetOrAddNode(new Vector3(totalArea.xMin, 0, totalArea.yMax));

            // 2. 외곽 경계 생성
            DrawBoundary(totalArea);

            // 3. 재귀적 공간 분할 (코너 정보 동기화)
            Subdivide(totalArea, 0, tlNode, trNode, brNode, blNode);

            // 4. 노드 지터(Jitter) 적용 (자연스러운 그리드 형성)
            JitterNodes();

            // 4. 인접 리스트 재구축
            RebuildAdjacency();

            // // 5. 메쉬 생성 및 최적화
            GenerateMesh();


            sw.Stop();
            UnityEngine.Debug.Log($"도시 생성 완료: {nodes.Count} 노드, {uniqueSegments.Count} 도로 구간 (소요 시간: {sw.ElapsedMilliseconds}ms)");

#if UNITY_EDITOR
            // 에디터에서 데이터 변경을 저장하기 위해 더티 플래그 설정
            UnityEditor.EditorUtility.SetDirty(this);
            if (!Application.isPlaying)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }

        /// <summary>
        /// 인접한 노드들이 너무 가까울 경우 서로 끌어당겨 통합(Consolidate)하거나 
        /// 격자를 자연스럽게 조정합니다. (기존 척력 로직에서 통합 로직으로 변경)
        /// </summary>
        /// <summary>
        /// 1. 가까운 교차로들을 통합(Consolidate)하여 그래프를 단순화하고,
        /// 2. 도로가 겹치지 않는 범위 내에서 안전하게 지터(Jitter)를 적용합니다.
        /// </summary>
        private void JitterNodes()
        {
            // Step 1: 교차로 통합
            ConsolidateNodes();

            if (nodeJitter <= 0) return;

            // Step 2: 안전한 지터 적용
            float eps = 0.1f;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 p = nodes[i];

                // 외곽 경계 노드는 이동시키지 않음
                bool onBoundaryX = Mathf.Abs(p.x - (-citySize.x / 2f)) < eps || Mathf.Abs(p.x - (citySize.x / 2f)) < eps;
                bool onBoundaryZ = Mathf.Abs(p.z - (-citySize.y / 2f)) < eps || Mathf.Abs(p.z - (citySize.y / 2f)) < eps;
                if (onBoundaryX || onBoundaryZ) continue;

                // 최소 안전 거리 확보 (가장 가까운 이웃과의 거리의 40%까지만 이동 허용)
                float minNeighborDist = float.MaxValue;
                foreach (int nIdx in adjacency[i])
                {
                    float d = Vector3.Distance(nodes[i], nodes[nIdx]);
                    if (d < minNeighborDist) minNeighborDist = d;
                }

                float maxJitter = Mathf.Min(nodeJitter, minNeighborDist * 0.4f);
                if (maxJitter < 0.01f) continue;

                Vector3 randomOffset = new Vector3(
                    UnityEngine.Random.Range(-1f, 1f),
                    0,
                    UnityEngine.Random.Range(-1f, 1f)
                ).normalized * UnityEngine.Random.Range(0, maxJitter);

                nodes[i] += randomOffset;
            }
        }

        /// <summary>
        /// 서로 너무 가까운 노드들을 하나로 병합하고 도로 연결을 정리합니다.
        /// </summary>
        private void ConsolidateNodes()
        {
            float threshold = roadWidth * 1.5f; // 통합 거리 임계값
            bool changed = true;
            int maxIterations = 5; // 무한 루프 방지

            while (changed && maxIterations-- > 0)
            {
                changed = false;
                // 활성 노드 매핑 (oldIndex -> newIndex)
                Dictionary<int, int> redirectMap = new Dictionary<int, int>();
                List<Vector3> newNodes = new List<Vector3>();
                List<bool> merged = new List<bool>(new bool[nodes.Count]);

                // 그리드 기반으로 가까운 노드 그룹 찾기 (단순화된 방식: 아직 병합 안된 노드 기준 탐색)
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (merged[i]) continue;

                    // i를 중심으로 그룹 형성
                    List<int> group = new List<int> { i };
                    Vector3 centerSum = nodes[i];
                    merged[i] = true;

                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        if (merged[j]) continue;
                        if (Vector3.Distance(nodes[i], nodes[j]) < threshold)
                        {
                            group.Add(j);
                            centerSum += nodes[j];
                            merged[j] = true;
                            changed = true;
                        }
                    }

                    // 그룹의 평균 위치로 새 노드 생성
                    int newIdx = newNodes.Count;
                    newNodes.Add(centerSum / group.Count);

                    foreach (int oldIdx in group)
                    {
                        redirectMap[oldIdx] = newIdx;
                    }
                }

                if (changed)
                {
                    // 노드 리스트 갱신
                    nodes = newNodes;

                    // 세그먼트 리스트 갱신 (리맵핑 및 중복 제거)
                    HashSet<RoadSegment> newSegments = new HashSet<RoadSegment>();
                    foreach (var seg in uniqueSegments)
                    {
                        if (!redirectMap.ContainsKey(seg.a) || !redirectMap.ContainsKey(seg.b)) continue;
                        
                        int newA = redirectMap[seg.a];
                        int newB = redirectMap[seg.b];

                        if (newA != newB) // 자기 자신 연결(Loop) 제거
                        {
                            newSegments.Add(new RoadSegment(newA, newB));
                        }
                    }
                    uniqueSegments = newSegments;
                    
                    // 인접 리스트 즉시 갱신 (다음 반복이나 Jitter에서 사용)
                    RebuildAdjacency();
                }
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
        /// 주어진 영역을 가로 또는 세로로 랜덤하게 분할하며, 블록의 4개 코너 노드를 추적합니다.
        /// </summary>
        private void Subdivide(Rect area, int depth, int tl, int tr, int br, int bl)
        {
            if (depth >= maxDepth || (area.width < minBlockSize * 2f && area.height < minBlockSize * 2f))
            {
                // blocks.Add(new Block(tl, tr, br, bl));
                return;
            }

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

                int ml = GetOrAddNode(start);
                int mr = GetOrAddNode(end);

                Subdivide(new Rect(area.x, area.y, area.width, splitPos - area.yMin), depth + 1, ml, mr, br, bl);
                Subdivide(new Rect(area.x, splitPos, area.width, area.yMax - splitPos), depth + 1, tl, tr, mr, ml);
            }
            else
            {
                Vector3 start = new Vector3(splitPos, 0, area.yMin);
                Vector3 end = new Vector3(splitPos, 0, area.yMax);
                AddSplitLine(start, end);

                int mb = GetOrAddNode(start);
                int mt = GetOrAddNode(end);

                Subdivide(new Rect(area.x, area.y, splitPos - area.xMin, area.height), depth + 1, tl, mt, mb, bl);
                Subdivide(new Rect(splitPos, area.y, area.xMax - splitPos, area.height), depth + 1, mt, tr, br, mb);
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
        /// 2단계 프로세스로 메쉬를 생성합니다: 1. 교차로 형상 계산 및 생성, 2. 도로 세그먼트 연결.
        /// </summary>
        private void GenerateMesh()
        {
            if (meshFilter == null) return;

            List<Vector3> finalVerts = new List<Vector3>();
            List<Color> finalColors = new List<Color>();
            List<int> finalTris = new List<int>();

            // 교차로 데이터 초기화
            nodeJunctionCorners.Clear();

            // --- Pass 1: 교차로 형상 계산 및 생성 ---
            for (int i = 0; i < nodes.Count; i++)
            {
                CalculateAndGenerateJunction(i, finalVerts, finalColors, finalTris);
            }

            // --- Pass 2: 도로 세그먼트 생성 (이미 계산된 교차로 코너 연결) ---
            foreach (var seg in uniqueSegments)
            {
                GenerateRoadBetweenJunctions(seg, finalVerts, finalColors, finalTris);
            }

            // 최종 메쉬 조립
            Mesh mesh = new Mesh();
            mesh.name = "CityNetwork_JunctionFirst";
            mesh.vertices = finalVerts.ToArray();
            mesh.colors = finalColors.ToArray();
            mesh.SetTriangles(finalTris.ToArray(), 0);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshFilter.mesh = mesh;
            
            // 콜라이더 업데이트
            if (GetComponent<MeshCollider>())
                GetComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void CalculateAndGenerateJunction(int nodeIdx, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            Vector3 center = nodes[nodeIdx];
            List<int> neighbors = adjacency[nodeIdx];
            int degree = neighbors.Count;
            float hw = roadWidth / 2f;

            // 기본 회전은 Identity (축 정렬)
            Quaternion rotation = Quaternion.identity;

            if (degree == 1)
            {
                Vector3 dir = (nodes[neighbors[0]] - center).normalized;
                rotation = Quaternion.LookRotation(dir);
            }
            // 3거리(T), 4거리(Cross) 등은 모두 기본 축 정렬 사각형 사용

            // 정사각형 코너 생성 (로컬 -> 월드 변환)
            Vector3[] localCorners = new Vector3[]
            {
                new Vector3(-hw, 0, -hw), // BL
                new Vector3(-hw, 0,  hw), // TL
                new Vector3( hw, 0,  hw), // TR
                new Vector3( hw, 0, -hw)  // BR
            };

            Vector3[] worldCorners = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                worldCorners[i] = center + rotation * localCorners[i];
            }

            // 메쉬 등록
            int startIdx = verts.Count;
            for (int i = 0; i < 4; i++)
            {
                verts.Add(worldCorners[i]);
                colors.Add(Color.white);
            }

            // CCW Winding
            tris.Add(startIdx + 0); tris.Add(startIdx + 1); tris.Add(startIdx + 2);
            tris.Add(startIdx + 0); tris.Add(startIdx + 2); tris.Add(startIdx + 3);

            // 데이터 저장
            nodeJunctionCorners[nodeIdx] = worldCorners;
        }

        private void GenerateRoadBetweenJunctions(RoadSegment seg, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            if (!nodeJunctionCorners.ContainsKey(seg.a) || !nodeJunctionCorners.ContainsKey(seg.b)) return;

            Vector3[] cA = nodeJunctionCorners[seg.a];
            Vector3[] cB = nodeJunctionCorners[seg.b];
            Vector3 pA = nodes[seg.a];
            Vector3 pB = nodes[seg.b];

            // A 교차로에서 B를 향하는 가장 가까운 두 코너 찾기
            // 사각형 코너 중 도로 방향(pB - pA)과 내적했을 때 가장 값이 큰 두 점이 연결점
            Vector3 dir = (pB - pA).normalized;
            
            var sortedA = cA.Select((p, i) => new { p, i, mod = Vector3.Dot((p - pA).normalized, dir) })
                            .OrderByDescending(x => x.mod).Take(2).ToArray();
            
            // B 교차로에서는 A를 향하는 방향(-dir)으로 가장 가까운 두 코너 찾기
            var sortedB = cB.Select((p, i) => new { p, i, mod = Vector3.Dot((p - pB).normalized, -dir) })
                            .OrderByDescending(x => x.mod).Take(2).ToArray();

            // 연결 쌍 매칭 (꼬임 방지)
            // A1-B1, A2-B2 연결 vs A1-B2, A2-B1 연결 중 거리 합이 짧은 쪽 선택
            Vector3 a1 = sortedA[0].p;
            Vector3 a2 = sortedA[1].p;
            Vector3 b1 = sortedB[0].p;
            Vector3 b2 = sortedB[1].p;

            float distLinear = Vector3.Distance(a1, b1) + Vector3.Distance(a2, b2);
            float distCross = Vector3.Distance(a1, b2) + Vector3.Distance(a2, b1);

            Vector3 finalA1 = a1, finalA2 = a2, finalB1 = b1, finalB2 = b2;
            if (distCross < distLinear)
            {
                finalB1 = b2;
                finalB2 = b1;
            }

            // 메쉬 생성 (A1, A2, B1, B2 순서 가정 - Quad)
            // 면의 방향(Normal)을 위쪽으로 맞추기 위해 외적 등을 확인해야 하지만, 
            // 2D 평면이므로 단순 정렬로도 충분 (보통 시계방향/반시계방향 일관성 있으면 됨)

            // 순서 정렬: A1 -> B1 -> B2 -> A2 (반시계)
            // 벡터 (A2-A1) 외적 (B1-A1) 이 Y축 양수면 OK
            Vector3 cross = Vector3.Cross(finalB1 - finalA1, finalA2 - finalA1);
            if (cross.y < 0) 
            {
                // 뒤집힘
                Vector3 temp = finalA1; finalA1 = finalA2; finalA2 = temp;
                temp = finalB1; finalB1 = finalB2; finalB2 = temp;
            }

            int startIdx = verts.Count;
            verts.Add(finalA1); colors.Add(Color.white); // 0
            verts.Add(finalB1); colors.Add(Color.white); // 1
            verts.Add(finalB2); colors.Add(Color.white); // 2
            verts.Add(finalA2); colors.Add(Color.white); // 3

            tris.Add(startIdx + 0); tris.Add(startIdx + 1); tris.Add(startIdx + 2);
            tris.Add(startIdx + 0); tris.Add(startIdx + 2); tris.Add(startIdx + 3);
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
        /// 격자 공간 분할을 사용하여 지정된 거리(Tolerance) 내의 중복 정점들을 병합합니다.
        /// </summary>
        private void WeldVertices(Mesh mesh, bool logResult = true, float ratio = 1.0f)
        {
            Vector3[] oldVerts = mesh.vertices;
            Color[] oldColors = mesh.colors;
            List<Vector3> newVerts = new List<Vector3>();
            List<Color> newColors = new List<Color>();
            
            int submeshCount = mesh.subMeshCount;
            List<int[]> newSubmeshTris = new List<int[]>();
            
            // Scaled quantization to allow stable floating point comparison
            float invTol = 1f / Mathf.Max(0.001f, weldTolerance * ratio);
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
            nodeJunctionCorners.Clear();
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

        #region Serialization
        public void OnBeforeSerialize()
        {
            // HashSet -> List 변환
            serializedSegments.Clear();
            foreach (var seg in uniqueSegments) serializedSegments.Add(seg);

            // Dictionary -> List 변환
            junctionKeys.Clear();
            junctionValues.Clear();
            foreach (var kvp in nodeJunctionCorners)
            {
                if (kvp.Value == null || kvp.Value.Length != 4) continue;
                junctionKeys.Add(kvp.Key);
                junctionValues.Add(new Vector3ArrayWrapper { array = kvp.Value });
            }
        }

        public void OnAfterDeserialize()
        {
            // List -> HashSet 복구
            uniqueSegments.Clear();
            if (serializedSegments != null)
            {
                foreach (var seg in serializedSegments) uniqueSegments.Add(seg);
            }

            // List -> Dictionary 복구
            nodeJunctionCorners.Clear();
            if (junctionKeys != null && junctionValues != null && junctionKeys.Count == junctionValues.Count)
            {
                for (int i = 0; i < junctionKeys.Count; i++)
                {
                    nodeJunctionCorners[junctionKeys[i]] = junctionValues[i].array;
                }
            }

            // 인접 리스트 재구축 (기즈모 및 생성 로직용)
            if (nodes != null && nodes.Count > 0)
            {
                RebuildAdjacency();
            }
        }
        #endregion

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
#if UNITY_EDITOR
                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(nodes[i] + Vector3.up * 0.5f, i.ToString());
#endif
            }
        }
    }
}
