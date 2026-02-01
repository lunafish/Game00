using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class TerrainGenerator : MonoBehaviour
{
    public ComputeShader heightmapCompute;
    public Mesh baseMesh; 
    
    [Header("Generation Settings")]
    public int resolution = 50; 
    public float size = 10f;

    [Header("Noise Settings")]
    public float scale = 0.2f;
    public float heightMultiplier = 1.5f;
    public Vector3 offset;
    [Range(1, 8)] public int octaves = 4;
    [Range(0, 1)] public float persistence = 0.5f;
    
    struct VertexData {
        public Vector3 position;
        public Vector3 normal;
    }

    private Mesh generatedMesh;
    private ComputeBuffer vertexBuffer;
    private VertexData[] vertexData;

    void Start()
    {
        GenerateTerrain();
    }

    [ContextMenu("Generate Terrain")]
    public void GenerateTerrain()
    {
        if (heightmapCompute == null) {
            Debug.LogError("Heightmap Compute Shader가 할당되지 않았습니다.");
            return;
        }

        Mesh sourceMesh = baseMesh;
        
        // baseMesh가 없거나 해상도 자동 생성 조건일 경우 Plane 생성
        if (sourceMesh == null || resolution > 10) 
        {
            sourceMesh = CreateSubdividedPlane(resolution, size);
        }

        Vector3[] vertices = sourceMesh.vertices;
        Vector3[] normals = sourceMesh.normals;
        int vertexCount = vertices.Length;

        vertexData = new VertexData[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertexData[i].position = vertices[i];
            vertexData[i].normal = normals[i];
        }

        vertexBuffer = new ComputeBuffer(vertexCount, Marshal.SizeOf(typeof(VertexData)));
        vertexBuffer.SetData(vertexData);

        int kernel = heightmapCompute.FindKernel("CSMain");
        heightmapCompute.SetBuffer(kernel, "Vertices", vertexBuffer);
        heightmapCompute.SetFloat("scale", scale);
        heightmapCompute.SetFloat("heightMultiplier", heightMultiplier);
        heightmapCompute.SetVector("offset", offset);
        heightmapCompute.SetInt("octaves", octaves);
        heightmapCompute.SetFloat("persistence", persistence);
        
        heightmapCompute.Dispatch(kernel, Mathf.CeilToInt(vertexCount / 64f), 1, 1);

        vertexBuffer.GetData(vertexData);

        if (generatedMesh == null) {
            generatedMesh = new Mesh();
            generatedMesh.name = "Procedural Terrain";
            generatedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        
        Vector3[] newVertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++) newVertices[i] = vertexData[i].position;

        generatedMesh.Clear();
        generatedMesh.vertices = newVertices;
        generatedMesh.triangles = sourceMesh.triangles;
        generatedMesh.uv = sourceMesh.uv;
        generatedMesh.RecalculateNormals();
        generatedMesh.RecalculateBounds();

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null) mf.sharedMesh = generatedMesh;
        
        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = generatedMesh;

        vertexBuffer.Release();
        vertexBuffer = null;
    }

    // 고해상도 평면 메쉬를 코드로 생성
    Mesh CreateSubdividedPlane(int res, float size)
    {
        Mesh mesh = new Mesh();
        mesh.name = "Subdivided Plane";

        List<Vector3> verts = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> tris = new List<int>();

        float step = size / (res - 1);
        float start = -size * 0.5f;

        for (int y = 0; y < res; y++) {
            for (int x = 0; x < res; x++) {
                verts.Add(new Vector3(start + x * step, 0, start + y * step));
                uvs.Add(new Vector2((float)x / (res - 1), (float)y / (res - 1)));
            }
        }

        for (int y = 0; y < res - 1; y++) {
            for (int x = 0; x < res - 1; x++) {
                int i = x + y * res;
                tris.Add(i); tris.Add(i + res); tris.Add(i + res + 1);
                tris.Add(i); tris.Add(i + res + 1); tris.Add(i + 1);
            }
        }

        mesh.vertices = verts.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    void OnDestroy()
    {
        if (vertexBuffer != null) vertexBuffer.Release();
    }
}
