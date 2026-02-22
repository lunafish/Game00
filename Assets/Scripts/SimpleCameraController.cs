using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

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
    
    [Header("Touch Settings")]
    public float touchSensitivityX = 0.2f; // 터치 좌우 감도
    public float touchSensitivityY = 0.2f; // 터치 상하 감도
    public float touchZoomSpeed = 0.01f;   // 터치 줌 속도

    [Header("Limitations")]
    public float minVerticalAngle = -20f; // 아래로 내려다보는 최대 각도 (제한)
    public float maxVerticalAngle = 80f;  // 위로 올려다보는 최대 각도

    private float currentX = 0f;
    private float currentY = 0f;

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
    }

    void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    void Start()
    {
        // 모바일 플랫폼에서는 커서를 보이게 하고 잠금 해제
        if (Application.isMobilePlatform)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            // PC 등에서는 커서 잠금 및 숨김
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

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

        float inputX = 0f;
        float inputY = 0f;
        float zoomDelta = 0f;

        // 1. 터치 입력 처리 (EnhancedTouch 사용)
        if (Touch.activeTouches.Count > 0)
        {
            // 한 손가락: 회전
            if (Touch.activeTouches.Count == 1)
            {
                var touch = Touch.activeTouches[0];
                if (touch.phase == UnityEngine.InputSystem.TouchPhase.Moved)
                {
                    inputX = touch.delta.x * touchSensitivityX;
                    inputY = touch.delta.y * touchSensitivityY;
                }
            }
            // 두 손가락: 핀치 줌
            else if (Touch.activeTouches.Count >= 2)
            {
                var touch0 = Touch.activeTouches[0];
                var touch1 = Touch.activeTouches[1];

                // 이전 프레임의 터치 위치 계산
                Vector2 touch0PrevPos = touch0.screenPosition - touch0.delta;
                Vector2 touch1PrevPos = touch1.screenPosition - touch1.delta;

                // 이전 거리와 현재 거리 비교
                float prevTouchDeltaMag = (touch0PrevPos - touch1PrevPos).magnitude;
                float touchDeltaMag = (touch0.screenPosition - touch1.screenPosition).magnitude;

                // 거리가 멀어지면(확대) distance 감소, 가까워지면(축소) distance 증가
                // deltaMagnitudeDiff > 0 (가까워짐) -> 줌 아웃
                // deltaMagnitudeDiff < 0 (멀어짐) -> 줌 인
                float deltaMagnitudeDiff = prevTouchDeltaMag - touchDeltaMag;

                zoomDelta = deltaMagnitudeDiff * touchZoomSpeed;
            }
        }
        // 2. 마우스 입력 처리 (터치가 없을 때)
        else if (Mouse.current != null)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            inputX = delta.x * sensitivityX;
            inputY = delta.y * sensitivityY;
            
            float scroll = Mouse.current.scroll.y.ReadValue();
            if (Mathf.Abs(scroll) > 0.1f)
            {
                zoomDelta = -scroll * zoomSpeed * 0.01f;
            }
        }

        // 3. 회전 적용
        currentX += inputX;
        currentY -= inputY; // 마우스/터치 위로 -> Pitch 감소 (위를 봄)
        
        currentY = Mathf.Clamp(currentY, minVerticalAngle, maxVerticalAngle);

        // 4. 줌 적용
        distance += zoomDelta;
        distance = Mathf.Clamp(distance, minDistance, maxDistance);

        // 5. 위치 및 회전 최종 적용
        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0);
        
        // 타겟 위치에서 Rotation * Distance 만큼 뒤로 뺌
        Vector3 negDistance = new Vector3(0.0f, 0.0f, -distance);
        Vector3 position = rotation * negDistance + (target.position + offset);

        transform.rotation = rotation;
        transform.position = position;
    }
}
