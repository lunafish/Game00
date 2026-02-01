using UnityEngine;
using UnityEngine.InputSystem;

public class SimpleCameraController : MonoBehaviour
{
    [Header("Target Settings")]
    public Transform target;            // 캐릭터 (Walker)
    public Vector3 offset = new Vector3(0, 2f, 0); // 타겟의 중심점 오프셋

    [Header("Control Settings")]
    public float sensitivityX = 2f;     // 마우스 좌우 감도
    public float sensitivityY = 0.2f;   // 마우스 상하 감도 (너무 빠르지 않게 조정)
    public float distance = 5f;         // 카메라 거리
    public float minDistance = 2f;      // 최소 거리 (줌인)
    public float maxDistance = 10f;     // 최대 거리 (줌아웃)
    public float zoomSpeed = 0.5f;      // 줌 속도
    
    [Header("Limitations")]
    public float minVerticalAngle = -20f; // 아래로 내려다보는 최대 각도 (제한)
    public float maxVerticalAngle = 80f;  // 위로 올려다보는 최대 각도

    private float currentX = 0f;
    private float currentY = 0f;

    void Start()
    {
        // 커서 잠금 및 숨김 (게임 플레이 시 필수)
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // 초기 각도 설정 (현재 카메라 각도 유지)
        Vector3 angles = transform.eulerAngles;
        currentX = angles.y;
        currentY = angles.x;

        // 타겟이 없다면 태그로 찾아보거나 경고
        if (target == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) target = player.transform;
        }
    }

    void LateUpdate()
    {
        if (target == null) return;

        // 1. 입력 수집 (Input System)
        float mouseX = 0;
        float mouseY = 0;
        float scroll = 0;

        if (Mouse.current != null)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            mouseX = delta.x * sensitivityX;
            mouseY = delta.y * sensitivityY;
            scroll = Mouse.current.scroll.y.ReadValue();
        }

        // 2. 회전 계산
        currentX += mouseX;
        currentY -= mouseY; // 마우스 위로(+Y) -> 카메라는 위를 봐야 함? 
                            // 보통 FPS에서 마우스 위로 = 고개 듬 = Pitch 감소(X축 회전 마이너스).
                            // Unity: Euler X 0=Horizon, 90=Down, -90=Up.
                            // So decreasing Y (Pitch) looks UP. 
        
        currentY = Mathf.Clamp(currentY, minVerticalAngle, maxVerticalAngle);

        // 3. 줌 (거리 조절)
        if (Mathf.Abs(scroll) > 0.1f)
        {
            distance -= scroll * zoomSpeed * 0.01f;
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }

        // 4. 위치 및 회전 적용
        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0);
        
        // 타겟 위치에서 Rotation * Distance 만큼 뒤로 뺌
        Vector3 negDistance = new Vector3(0.0f, 0.0f, -distance);
        Vector3 position = rotation * negDistance + (target.position + offset);

        transform.rotation = rotation;
        transform.position = position;
    }
}
