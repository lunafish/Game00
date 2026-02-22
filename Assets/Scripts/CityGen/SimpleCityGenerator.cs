using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

namespace CityGen
{
    /// <summary>
    /// 재귀적 분할(BSP) 알고리즘을 사용하여 도시 도로망을 생성하고 메쉬를 형성하는 클래스입니다.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class SimpleCityGenerator : MonoBehaviour, ISerializationCallbackReceiver
    {
        [Header("분할 설정 (Subdivision Settings)")]
        public int seed = 1234;
        public Vector2 citySize = new Vector2(100, 100);
        public int maxDepth = 5;
        public float minBlockSize = 20f;
        [Range(0, 0.4f)] public float splitJitter = 0.1f;
        [Range(0, 5f)] public float nodeJitter = 1.0f;

        [Header("건물 및 부지 설정 (Building & Lot Settings)")]
        public int subdivisionDepth = 2;
        public float minLotArea = 100f;
        [Range(0, 0.45f)] public float lotSplitJitter = 0.2f;
        
        public float minFloorHeight = 2.5f;
        public float maxFloorHeight = 4.5f;
        public int minFloors = 2;
        public int maxFloors = 10;
        
        public Material buildingMaterial;

        [Header("메쉬 설정 (Mesh Settings)")]
        public float roadWidth = 2.0f;
        public Material roadMaterial;
        [Range(0.001f, 2.0f)] public float weldTolerance = 2.0f;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;

        [Header("디버그 설정 (Debug Settings)")]
        public bool showRoadNodes = true;
        public bool showLogicalBlocks = false;
        public bool showBuildableAreas = true;
        public bool showSubLots = true;
        public bool showLotVertices = false; // New: 바닥 메쉬 정점 확인용
        public bool debugRefineLogic = false; // New: T-Junction 보정 로직 디버그

        // --- 그래프 데이터 ---
        [SerializeField, HideInInspector] protected List<Vector3> nodes = new List<Vector3>();
        protected HashSet<RoadSegment> uniqueSegments = new HashSet<RoadSegment>();
        protected List<List<int>> adjacency = new List<List<int>>();
        private Dictionary<int, Vector3[]> _nodeJunctionCorners = new Dictionary<int, Vector3[]>();
        [SerializeField, HideInInspector] protected List<CityBlock> cityBlocks = new List<CityBlock>();
        private Transform _buildingRoot;
        
        // 디버그용 정점 리스트
        private List<Vector3> _debugLotVerts = new List<Vector3>();


        // --- 직렬화 데이터 ---
        [SerializeField, HideInInspector] private List<RoadSegment> serializedSegments = new List<RoadSegment>();
        [SerializeField, HideInInspector] private List<int> junctionKeys = new List<int>();
        [SerializeField, HideInInspector] private List<Vector3ArrayWrapper> junctionValues = new List<Vector3ArrayWrapper>();

        [System.Serializable]
        public struct Vector3ArrayWrapper { public Vector3[] array; }

        [System.Serializable]
        public struct CityBlock
        {
            public int tl, tr, br, bl; // Core topological corners
            public List<int> fullPerimeter; // All nodes on the loop (including T-junctions)
            public Vector3[] innerPolygon; // Final road-adjusted vertices
            public List<Vector3ArrayWrapper> subLots; // New: recursively divided lots
            public CityBlock(int tl, int tr, int br, int bl)
            {
                this.tl = tl; this.tr = tr; this.br = br; this.bl = bl;
                this.fullPerimeter = null;
                this.innerPolygon = null;
                this.subLots = null;
            }
        }

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
            Random.InitState(seed);

            Rect totalArea = new Rect(-citySize.x / 2f, -citySize.y / 2f, citySize.x, citySize.y);

            // 1. 외곽 경계 생성
            DrawBoundary(totalArea);

            // 2. 재귀적 공간 분할 (도로망 생성)
            int blNode = GetOrAddNode(new Vector3(totalArea.xMin, 0, totalArea.yMin));
            int brNode = GetOrAddNode(new Vector3(totalArea.xMax, 0, totalArea.yMin));
            int trNode = GetOrAddNode(new Vector3(totalArea.xMax, 0, totalArea.yMax));
            int tlNode = GetOrAddNode(new Vector3(totalArea.xMin, 0, totalArea.yMax));
            Subdivide(totalArea, 0, tlNode, trNode, brNode, blNode);

            // 3. 노드 정리 및 지터(Jitter) 적용 (자연스러운 그리드 형성)
            JitterNodes();
            RebuildAdjacency();

            // 4. 도로 메쉬 생성
            GenerateMesh();

            // 5. 블록 및 건물 부지 계산
            CalculateBlockShapes();
            SubdivideAllBlocks();

            // 6. 부지 정점 보정 (T-Junction 해결)
            RefineLotShapes();

            // 7. 건물 부지 메쉬 생성
            GenerateBuildingMeshes();
            
            // 8. 연결 정보 로그 출력
            LogLotConnectivity();

            sw.Stop();
            Debug.Log($"도시 생성 완료: {nodes.Count} 노드, {uniqueSegments.Count} 도로 구간 (소요 시간: {sw.ElapsedMilliseconds}ms)");

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
                    Random.Range(-1f, 1f),
                    0,
                    Random.Range(-1f, 1f)
                ).normalized * Random.Range(0, maxJitter);

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
                    // 블록 리스트 리매핑 (핵심 코너 업데이트)
                    for (int k = 0; k < cityBlocks.Count; k++)
                    {
                        CityBlock b = cityBlocks[k];
                        if (redirectMap.ContainsKey(b.tl)) b.tl = redirectMap[b.tl];
                        if (redirectMap.ContainsKey(b.tr)) b.tr = redirectMap[b.tr];
                        if (redirectMap.ContainsKey(b.br)) b.br = redirectMap[b.br];
                        if (redirectMap.ContainsKey(b.bl)) b.bl = redirectMap[b.bl];
                        cityBlocks[k] = b;
                    }

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
                cityBlocks.Add(new CityBlock(tl, tr, br, bl));
                return;
            }

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
                if (Vector3.Distance(splitPoints[i], splitPoints[i + 1]) > 0.1f)
                {
                    int a = GetOrAddNode(splitPoints[i]);
                    int b = GetOrAddNode(splitPoints[i + 1]);
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
            _nodeJunctionCorners.Clear();

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
            _nodeJunctionCorners[nodeIdx] = worldCorners;
        }

        private void GenerateRoadBetweenJunctions(RoadSegment seg, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            if (!_nodeJunctionCorners.ContainsKey(seg.a) || !_nodeJunctionCorners.ContainsKey(seg.b)) return;

            Vector3[] cA = _nodeJunctionCorners[seg.a];
            Vector3[] cB = _nodeJunctionCorners[seg.b];
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

        /// <summary>
        /// 모든 블록에 대해 둘레 노드를 추적하고, 도로 폭이 반영된 정밀한 안쪽 다각형을 계산합니다.
        /// </summary>
        private void CalculateBlockShapes()
        {
            for (int i = 0; i < cityBlocks.Count; i++)
            {
                CityBlock block = cityBlocks[i];
                if (block.tl >= nodes.Count || block.tr >= nodes.Count || block.br >= nodes.Count || block.bl >= nodes.Count) continue;

                // 1. Trace exact perimeter nodes using graph traversal
                List<int> perimeter = new List<int>();
                perimeter.Add(block.tl);
                perimeter.AddRange(FindPathBetweenCorners(block.tl, block.tr));
                perimeter.AddRange(FindPathBetweenCorners(block.tr, block.br));
                perimeter.AddRange(FindPathBetweenCorners(block.br, block.bl));
                perimeter.AddRange(FindPathBetweenCorners(block.bl, block.tl));

                for (int j = perimeter.Count - 1; j > 0; j--)
                    if (perimeter[j] == perimeter[j - 1]) perimeter.RemoveAt(j);
                if (perimeter.Count > 1 && perimeter[0] == perimeter[perimeter.Count - 1]) perimeter.RemoveAt(perimeter.Count - 1);

                block.fullPerimeter = perimeter;

                // 2. Assemble exact inner polygon by collecting corners from both incoming and outgoing segments
                List<Vector3> polygonVerts = new List<Vector3>();
                for (int j = 0; j < perimeter.Count; j++)
                {
                    int prev = perimeter[(j + perimeter.Count - 1) % perimeter.Count];
                    int curr = perimeter[j];
                    int next = perimeter[(j + 1) % perimeter.Count];

                    // A. End-Left corner of incoming segment (prev -> curr)
                    polygonVerts.Add(GetSpecificRoadCorner(curr, prev, true, false));

                    // B. Start-Left corner of outgoing segment (curr -> next)
                    polygonVerts.Add(GetSpecificRoadCorner(curr, next, true, true));
                }

                // Remove consecutive duplicates (in case incoming and outgoing corners are the same)
                for (int j = polygonVerts.Count - 1; j > 0; j--)
                {
                    if (Vector3.Distance(polygonVerts[j], polygonVerts[j - 1]) < 0.001f)
                        polygonVerts.RemoveAt(j);
                }
                if (polygonVerts.Count > 1 && Vector3.Distance(polygonVerts[0], polygonVerts[polygonVerts.Count - 1]) < 0.001f)
                    polygonVerts.RemoveAt(polygonVerts.Count - 1);

                block.innerPolygon = polygonVerts.ToArray();
                cityBlocks[i] = block;
            }
        }

        private void GenerateBuildingMeshes()
        {
            // 기존 건물 루트 제거
            if (_buildingRoot != null)
            {
                if (Application.isPlaying) Destroy(_buildingRoot.gameObject);
                else DestroyImmediate(_buildingRoot.gameObject);
            }
            
            _buildingRoot = new GameObject("Buildings").transform;
            _buildingRoot.SetParent(transform, false);

            _debugLotVerts.Clear();

            for (int bIdx = 0; bIdx < cityBlocks.Count; bIdx++)
            {
                var block = cityBlocks[bIdx];
                if (block.subLots == null || block.subLots.Count == 0) continue;

                // 1. 블록 내 모든 부지에 층수 및 층 높이 사전 할당
                int[] lotFloors = new int[block.subLots.Count];
                float[] lotFloorHeights = new float[block.subLots.Count];
                for (int i = 0; i < block.subLots.Count; i++)
                {
                    lotFloors[i] = Random.Range(minFloors, maxFloors + 1);
                    lotFloorHeights[i] = Random.Range(minFloorHeight, maxFloorHeight);
                }

                // 2. 엣지 공유 정보 맵핑 (엣지 -> 해당 엣지를 가진 부지들의 절대 높이 리스트)
                var edgeToHeights = new Dictionary<EdgeKey, List<float>>();
                for (int i = 0; i < block.subLots.Count; i++)
                {
                    Vector3[] poly = block.subLots[i].array;
                    if (poly == null) continue;
                    float totalHeight = lotFloors[i] * lotFloorHeights[i];
                    for (int j = 0; j < poly.Length; j++)
                    {
                        EdgeKey key = new EdgeKey(poly[j], poly[(j + 1) % poly.Length]);
                        if (!edgeToHeights.ContainsKey(key)) edgeToHeights[key] = new List<float>();
                        edgeToHeights[key].Add(totalHeight);
                    }
                }

                // 3. 개별 건물 메쉬 생성
                for (int i = 0; i < block.subLots.Count; i++)
                {
                    Vector3[] poly = block.subLots[i].array;
                    if (poly == null || poly.Length < 3) continue;

                    // 개별 빌딩을 위한 데이터
                    List<Vector3> bVerts = new List<Vector3>();
                    List<int> bTris = new List<int>();
                    List<Vector2> bUvs = new List<Vector2>();
                    int vertOffset = 0;

                    int myFloors = lotFloors[i];
                    float myFloorHeight = lotFloorHeights[i];
                    float myTotalHeight = myFloors * myFloorHeight;

                    // --- 지붕 생성 ---
                    int roofStartIdx = vertOffset;
                    for (int j = 0; j < poly.Length; j++)
                    {
                        Vector3 pos = poly[j] + Vector3.up * myTotalHeight; 
                        bVerts.Add(pos);
                        bUvs.Add(new Vector2(pos.x, pos.z) * 0.5f);
                    }
                    vertOffset += poly.Length;
                    for (int j = 1; j < poly.Length - 1; j++)
                    {
                        bTris.Add(roofStartIdx + 0);
                        bTris.Add(roofStartIdx + j);
                        bTris.Add(roofStartIdx + j + 1);
                    }

                    // --- 층별 외벽 생성 (최적화: 1층 별도, 2층 이상 통합) ---
                    for (int j = 0; j < poly.Length; j++)
                    {
                        Vector3 p1 = poly[j];
                        Vector3 p2 = poly[(j + 1) % poly.Length];
                        EdgeKey key = new EdgeKey(p1, p2);

                        float neighborMaxHeight = 0;
                        if (edgeToHeights.TryGetValue(key, out var heightsList))
                        {
                            foreach (float h in heightsList)
                            {
                                if (Mathf.Abs(h - myTotalHeight) > 0.01f || heightsList.Count(x => Mathf.Abs(x - myTotalHeight) < 0.01f) > 1)
                                {
                                    neighborMaxHeight = Mathf.Max(neighborMaxHeight, h);
                                }
                            }
                        }

                        // 1. 1층 처리 (0 ~ myFloorHeight)
                        float f1Bottom = 0;
                        float f1Top = myFloorHeight;
                        if (f1Top > neighborMaxHeight + 0.01f)
                        {
                            float actualBottom = Mathf.Max(f1Bottom, neighborMaxHeight);
                            AddWallQuad(bVerts, bUvs, bTris, ref vertOffset, p1, p2, actualBottom, f1Top);
                        }

                        // 2. 2층 이상 통합 처리 (myFloorHeight ~ myTotalHeight)
                        if (myFloors > 1)
                        {
                            float restBottom = myFloorHeight;
                            float restTop = myTotalHeight;
                            if (restTop > neighborMaxHeight + 0.01f)
                            {
                                // 2층 시작점보다 이웃 건물이 높다면 이웃 건물 높이부터 시작
                                float actualBottom = Mathf.Max(restBottom, neighborMaxHeight);
                                AddWallQuad(bVerts, bUvs, bTris, ref vertOffset, p1, p2, actualBottom, restTop);
                            }
                        }
                    }

                    // --- 개별 오브젝트 생성 ---
                    if (bVerts.Count > 0)
                    {
                        Mesh mesh = new Mesh();
                        mesh.name = $"BuildingMesh_{bIdx}_{i}";
                        mesh.vertices = bVerts.ToArray();
                        mesh.triangles = bTris.ToArray();
                        mesh.uv = bUvs.ToArray();
                        mesh.RecalculateNormals();
                        mesh.RecalculateBounds();

                        GameObject go = new GameObject($"Building_{bIdx}_{i}");
                        go.transform.SetParent(_buildingRoot, false);
                        
                        go.AddComponent<MeshFilter>().mesh = mesh;
                        MeshRenderer mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = buildingMaterial != null ? buildingMaterial : meshRenderer.sharedMaterial;
                        go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    }
                }
            }
        }

        private void AddWallQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris, ref int offset, Vector3 p1, Vector3 p2, float bottomY, float topY)
        {
            verts.Add(p1 + Vector3.up * bottomY);
            verts.Add(p2 + Vector3.up * bottomY);
            verts.Add(p2 + Vector3.up * topY);
            verts.Add(p1 + Vector3.up * topY);

            float len = Vector3.Distance(p1, p2);
            float h = topY - bottomY;
            uvs.Add(new Vector2(0, 0));
            uvs.Add(new Vector2(len, 0));
            uvs.Add(new Vector2(len, h));
            uvs.Add(new Vector2(0, h));

            tris.Add(offset + 0);
            tris.Add(offset + 1);
            tris.Add(offset + 2);
            tris.Add(offset + 0);
            tris.Add(offset + 2);
            tris.Add(offset + 3);

            offset += 4;
        }

        private void LogLotConnectivity()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Lot Connectivity Report ===");

            int totalBlocksWithGaps = 0;

            for (int i = 0; i < cityBlocks.Count; i++)
            {
                var block = cityBlocks[i];
                if (block.subLots == null || block.subLots.Count == 0) continue;

                Dictionary<string, int> edgeCounts = new Dictionary<string, int>();
                Dictionary<string, Vector3> edgeDirs = new Dictionary<string, Vector3>();

                // 정밀도 조정 (0.01f 단위)
                string VecKey(Vector3 v) => $"{v.x:F2},{v.z:F2}";
                string EdgeKey(Vector3 v1, Vector3 v2)
                {
                    string k1 = VecKey(v1);
                    string k2 = VecKey(v2);
                    return string.Compare(k1, k2) < 0 ? $"{k1}|{k2}" : $"{k2}|{k1}";
                }

                foreach (var lot in block.subLots)
                {
                    Vector3[] poly = lot.array;
                    if (poly == null) continue;
                    for (int j = 0; j < poly.Length; j++)
                    {
                        Vector3 p1 = poly[j];
                        Vector3 p2 = poly[(j + 1) % poly.Length];
                        string key = EdgeKey(p1, p2);

                        if (!edgeCounts.ContainsKey(key))
                        {
                            edgeCounts[key] = 0;
                            edgeDirs[key] = (p2 - p1).normalized;
                        }
                        edgeCounts[key]++;
                    }
                }

                // 공유되지 않은 엣지(Count == 1) 찾기
                List<string> openEdges = new List<string>();
                foreach(var kvp in edgeCounts)
                {
                    if(kvp.Value == 1) openEdges.Add(kvp.Key);
                }

                if (openEdges.Count > 0)
                {
                    totalBlocksWithGaps++;
                    sb.AppendLine($"[Block {i}] has {openEdges.Count} open edges (Boundary or Gap).");
                    // 처음 몇 개만 샘플링하여 방향 출력
                    for(int k=0; k<Mathf.Min(openEdges.Count, 10); k++)
                    {
                        string key = openEdges[k];
                        Vector3 dir = edgeDirs[key];
                        sb.AppendLine($"  - Edge {key} | Dir: {dir}");
                    }
                    if(openEdges.Count > 10) sb.AppendLine($"  ... and {openEdges.Count - 10} more.");
                }
            }
            Debug.Log(sb.ToString());
        }

        private void RefineLotShapes()
        {
            for (int i = 0; i < cityBlocks.Count; i++)
            {
                CityBlock block = cityBlocks[i];
                if (block.subLots == null || block.subLots.Count == 0) continue;

                // 1. Collect all vertices in the block to a unique list
                List<Vector3> allVerts = new List<Vector3>();
                foreach (var lot in block.subLots)
                {
                    if (lot.array != null) 
                    {
                        foreach(var v in lot.array) allVerts.Add(v);
                    }
                }

                // 2. Refine each lot
                List<Vector3ArrayWrapper> newSubLots = new List<Vector3ArrayWrapper>();
                foreach (var lot in block.subLots)
                {
                    if (lot.array == null) continue;
                    Vector3[] poly = lot.array;
                    List<Vector3> refinedPoly = new List<Vector3>();

                    for (int j = 0; j < poly.Length; j++)
                    {
                        Vector3 p1 = poly[j];
                        Vector3 p2 = poly[(j + 1) % poly.Length];
                        
                        refinedPoly.Add(p1);

                        if (debugRefineLogic)
                            Debug.Log($"[Refine] Block {i} Segment: {p1.ToString("F3")} -> {p2.ToString("F3")}");

                        // Find vertices on this segment
                        List<Vector3> pointsOnSegment = new List<Vector3>();
                        foreach (var v in allVerts)
                        {
                            // Skip if v is p1 or p2
                            if (Vector3.Distance(v, p1) < 0.01f || Vector3.Distance(v, p2) < 0.01f) continue;

                            // Check if v is on segment p1-p2
                            if (IsPointOnSegment(v, p1, p2))
                            {
                                if (debugRefineLogic)
                                    Debug.Log($"    -> Found Vertex on Segment: {v.ToString("F3")}");

                                // Check for duplicates in pointsOnSegment
                                bool exists = false;
                                foreach(var added in pointsOnSegment)
                                {
                                    if(Vector3.Distance(v, added) < 0.01f) { exists = true; break; }
                                }
                                if(!exists) pointsOnSegment.Add(v);
                            }
                        }

                        if (pointsOnSegment.Count > 0)
                        {
                            // Sort by distance from p1
                            pointsOnSegment.Sort((a, b) => Vector3.SqrMagnitude(a - p1).CompareTo(Vector3.SqrMagnitude(b - p1)));
                            refinedPoly.AddRange(pointsOnSegment);
                        }
                    }
                    newSubLots.Add(new Vector3ArrayWrapper { array = refinedPoly.ToArray() });
                }
                
                block.subLots = newSubLots;
                cityBlocks[i] = block;
            }
        }

        private bool IsPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 1e-9f) return false;

            // Project p onto ab, computing parameterized position t
            float t = Vector3.Dot(p - a, ab) / sqrLen;

            // Check if t is within the segment (excluding endpoints with a small epsilon)
            if (t < 0.01f || t > 0.99f) return false;

            // Compute the projection point
            Vector3 proj = a + t * ab;

            // Check distance from the line
            return (p - proj).sqrMagnitude < (0.05f * 0.05f);
        }

        private Vector3[] EnsureCCW(Vector3[] polygon)
        {
            float area = CalculatePolygonArea(polygon, true); // Get signed area
            if (area < 0) // It's CW, reverse it
            {
                Vector3[] reversed = new Vector3[polygon.Length];
                for (int i = 0; i < polygon.Length; i++)
                {
                    reversed[i] = polygon[polygon.Length - 1 - i];
                }
                return reversed;
            }
            return polygon;
        }

        private void SubdivideAllBlocks()
        {
            for (int i = 0; i < cityBlocks.Count; i++)
            {
                CityBlock b = cityBlocks[i];
                if (b.innerPolygon != null && b.innerPolygon.Length >= 3)
                {
                    b.subLots = new List<Vector3ArrayWrapper>();
                    SubdividePolygonRecursive(b.innerPolygon, 0, b.subLots);
                    cityBlocks[i] = b;
                }
            }
        }

        private void SubdividePolygonRecursive(Vector3[] polygon, int depth, List<Vector3ArrayWrapper> results)
        {
            float area = CalculatePolygonArea(polygon);
            if (depth >= subdivisionDepth || area < minLotArea)
            {
                results.Add(new Vector3ArrayWrapper { array = polygon });
                return;
            }

            // Simple split along longest bounding box axis
            Bounds b = new Bounds(polygon[0], Vector3.zero);
            foreach (var p in polygon) b.Encapsulate(p);

            float ratio = 0.5f + Random.Range(-lotSplitJitter, lotSplitJitter);

            Vector3 splitPoint;
            Vector3 splitDir;

            if (b.size.x > b.size.z)
            {
                splitPoint = new Vector3(b.min.x + b.size.x * ratio, b.center.y, b.center.z);
                splitDir = Vector3.right;
            }
            else
            {
                splitPoint = new Vector3(b.center.x, b.center.y, b.min.z + b.size.z * ratio);
                splitDir = Vector3.forward;
            }

            var split = SplitPolygon(polygon, splitPoint, splitDir);
            if (split.Item1 != null && split.Item2 != null && split.Item1.Length >= 3 && split.Item2.Length >= 3)
            {
                SubdividePolygonRecursive(split.Item1, depth + 1, results);
                SubdividePolygonRecursive(split.Item2, depth + 1, results);
            }
            else
            {
                results.Add(new Vector3ArrayWrapper { array = polygon });
            }
        }

        private (Vector3[], Vector3[]) SplitPolygon(Vector3[] polygon, Vector3 planePoint, Vector3 planeNormal)
        {
            List<Vector3> sideA = new List<Vector3>();
            List<Vector3> sideB = new List<Vector3>();

            for (int i = 0; i < polygon.Length; i++)
            {
                Vector3 p1 = polygon[i];
                Vector3 p2 = polygon[(i + 1) % polygon.Length];

                float d1 = Vector3.Dot(p1 - planePoint, planeNormal);
                float d2 = Vector3.Dot(p2 - planePoint, planeNormal);

                if (d1 >= 0) sideA.Add(p1);
                else sideB.Add(p1);

                if ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                {
                    float t = Mathf.Abs(d1) / (Mathf.Abs(d1) + Mathf.Abs(d2));
                    Vector3 intersect = Vector3.Lerp(p1, p2, t);
                    sideA.Add(intersect);
                    sideB.Add(intersect);
                }
            }
            return (sideA.ToArray(), sideB.ToArray());
        }

        private float CalculatePolygonArea(Vector3[] polygon, bool signed = false)
        {
            float area = 0f;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector3 p1 = polygon[i];
                Vector3 p2 = polygon[(i + 1) % polygon.Length];
                area += (p1.x * p2.z - p2.x * p1.z);
            }
            float result = area * 0.5f;
            return signed ? result : Mathf.Abs(result);
        }

        private Vector3 GetSpecificRoadCorner(int nodeIdx, int neighborIdx, bool getLeft, bool isStartOfSegment)
        {
            if (!_nodeJunctionCorners.ContainsKey(nodeIdx)) return nodes[nodeIdx];

            Vector3 center = nodes[nodeIdx];
            Vector3 neighborPos = nodes[neighborIdx];
            Vector3 dir = (neighborPos - center).normalized;
            Vector3[] corners = _nodeJunctionCorners[nodeIdx];

            // 1. Find the two corners facing the neighbor (same logic as GenerateRoadBetweenJunctions)
            var sorted = corners.Select((p, i) => new { p, mod = Vector3.Dot((p - center).normalized, dir) })
                                .OrderByDescending(x => x.mod).Take(2).ToArray();

            Vector3 c1 = sorted[0].p;
            Vector3 c2 = sorted[1].p;

            // 2. Look for the "Left" side relative to the road direction.
            Vector3 refDir = isStartOfSegment ? dir : -dir;

            float cross1 = Vector3.Cross(refDir, (c1 - center).normalized).y;
            float cross2 = Vector3.Cross(refDir, (c2 - center).normalized).y;

            if (getLeft) return cross1 > cross2 ? c1 : c2;
            else return cross1 < cross2 ? c1 : c2;
        }

        /// <summary>
        /// 두 코너 노드 사이의 최단 경로(도로 세그먼트 체인)를 찾아 반환합니다.
        /// </summary>
        private List<int> FindPathBetweenCorners(int startIdx, int endIdx)
        {
            List<int> path = new List<int>();
            if (startIdx == endIdx) return path;

            // BFS to find the shortest path in the road graph
            Queue<int> queue = new Queue<int>();
            Dictionary<int, int> parentMap = new Dictionary<int, int>();
            HashSet<int> visited = new HashSet<int>();

            queue.Enqueue(startIdx);
            visited.Add(startIdx);

            bool found = false;
            while (queue.Count > 0)
            {
                int curr = queue.Dequeue();
                if (curr == endIdx)
                {
                    found = true;
                    break;
                }

                foreach (int neighbor in adjacency[curr])
                {
                    if (!visited.Contains(neighbor))
                    {
                        visited.Add(neighbor);
                        parentMap[neighbor] = curr;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (found)
            {
                int step = endIdx;
                while (step != startIdx)
                {
                    path.Add(step);
                    step = parentMap[step];
                }
                path.Reverse();
            }

            return path;
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

        protected void ClearGraph()
        {
            nodes.Clear();
            uniqueSegments.Clear();
            adjacency.Clear();
            _nodeJunctionCorners.Clear();
            cityBlocks.Clear();
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

        private struct EdgeKey : System.IEquatable<EdgeKey>
        {
            public Vector3Int a, b;
            public EdgeKey(Vector3 v1, Vector3 v2)
            {
                Vector3Int q1 = Vector3Int.RoundToInt(v1 * 100f);
                Vector3Int q2 = Vector3Int.RoundToInt(v2 * 100f);
                if (q1.x < q2.x || (q1.x == q2.x && q1.z < q2.z)) { a = q1; b = q2; }
                else { a = q2; b = q1; }
            }
            public bool Equals(EdgeKey other) => a == other.a && b == other.b;
            public override int GetHashCode() => System.HashCode.Combine(a, b);
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
            foreach (var kvp in _nodeJunctionCorners)
            {
                if (kvp.Value == null) continue;
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
            _nodeJunctionCorners.Clear();
            if (junctionKeys != null && junctionValues != null && junctionKeys.Count == junctionValues.Count)
            {
                for (int i = 0; i < junctionKeys.Count; i++)
                {
                    _nodeJunctionCorners[junctionKeys[i]] = junctionValues[i].array;
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
            if (showRoadNodes)
            {
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

            // Draw City Blocks
            for (int i = 0; i < cityBlocks.Count; i++)
            {
                CityBlock b = cityBlocks[i];

                // 1. Draw core boundary (BFS style corners)
                if (showLogicalBlocks && b.tl < nodes.Count && b.tr < nodes.Count && b.br < nodes.Count && b.bl < nodes.Count)
                {
                    Gizmos.color = new Color(0, 0, 1, 0.3f); // Blue for logical rect
                    Gizmos.DrawLine(nodes[b.tl], nodes[b.tr]);
                    Gizmos.DrawLine(nodes[b.tr], nodes[b.br]);
                    Gizmos.DrawLine(nodes[b.br], nodes[b.bl]);
                    Gizmos.DrawLine(nodes[b.bl], nodes[b.tl]);

                    Vector3 center = (nodes[b.tl] + nodes[b.tr] + nodes[b.br] + nodes[b.bl]) / 4f;
#if UNITY_EDITOR
                    UnityEditor.Handles.color = Color.white;
                    UnityEditor.Handles.Label(center + Vector3.up * 1.5f, $"BLOCK #{i}");
#endif
                }

                // Draw refined inner polygon
                if (b.innerPolygon != null && b.innerPolygon.Length >= 3)
                {
                    // Draw outer boundary of buildable area
                    if (showBuildableAreas)
                    {
                        Gizmos.color = new Color(0, 1, 0, 0.5f);
                        for (int j = 0; j < b.innerPolygon.Length; j++)
                        {
                            Gizmos.DrawLine(b.innerPolygon[j], b.innerPolygon[(j + 1) % b.innerPolygon.Length]);
                            Gizmos.DrawWireCube(b.innerPolygon[j], Vector3.one * 0.3f);
                        }
                    }

                    // Draw sub-lots
                    if (showSubLots && b.subLots != null)
                    {
                        Gizmos.color = new Color(1, 1, 0, 0.4f); // Yellow for lots
                        foreach (var lot in b.subLots)
                        {
                            for (int j = 0; j < lot.array.Length; j++)
                            {
                                Gizmos.DrawLine(lot.array[j], lot.array[(j + 1) % lot.array.Length]);
                            }
                        }
                    }
                }
            }
            
            // Draw Lot Vertices (Debug)
            if (showLotVertices && _debugLotVerts != null)
            {
                Gizmos.color = new Color(1, 0, 1, 0.8f); // Magenta
                foreach (var v in _debugLotVerts)
                {
                    Gizmos.DrawSphere(v, 0.1f);
                }
            }
        }
    }
}
