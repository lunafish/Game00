using UnityEngine;
using System.Collections;
using Unity.Collections;
using Unity.Jobs;

namespace ComputeIK
{
    /// <summary>
    /// 로봇의 절차적 보행(Procedural Walking)을 제어하는 클래스입니다.
    /// WASD 입력을 받아 발걸음 주기와 몸체 이동을 동기화하여 미끄러짐 없는 보행(Zero-Slip)을 구현합니다.
    /// </summary>
    public class BipedProceduralWalker : MonoBehaviour
    {
        [Header("Movement Settings")]
        public float moveSpeed = 3f;      // 전진 이동 속도
        public float turnSpeed = 10f;     // 몸체 회전 속도
        
        [Header("Gait Settings")]
        public Transform footTargetL;     // 왼쪽 발 IK 타겟
        public Transform footTargetR;     // 오른쪽 발 IK 타겟
        public float stepLength = 0.5f;   // 발걸음 보간 거리 (보폭)
        public float stepAngle = 25f;     // 제자리 회전 시 발을 뗄 수 있는 각도 임계값
        public float stepHeight = 0.3f;   // 발을 들어올리는 최대 높이
        public float stepDuration = 0.3f; // 한 발자국을 내딛는 데 걸리는 시간
        public float stepSpacing = 0.25f; // 좌우 발 사이의 간격 (중심 기준)
        public float bodyHeight = 0.3f;   // 지면으로부터 몸체 중심까지의 높이
        public float detectionOffset = 0.25f; // 지면 변화를 미리 감지하는 거리 (오프셋)
        public float normalSmoothSpeed = 15f; // 지면 법선 변화의 부드러움 정도
        public LayerMask groundLayer;     // 지면 감지를 위한 레이어

        [Header("Body Sway")]
        public float swayAmount = 0.1f;   // 보행 중 몸체의 좌우 흔들림 정도
        public float bounceAmount = 0.05f; // 보행 중 몸체의 상하 반동 정도

        private Vector3 lastBodyPos;
        private Vector3 velocity;         // 이동 '의도'를 담는 속도 벡터
        
        private Vector3 currentFootPosL;   // 왼쪽 발의 현재 월드 위치
        private Vector3 currentFootPosR;   // 오른쪽 발의 현재 월드 위치
        private Vector3 targetFootPosL;
        private Vector3 targetFootPosR;
        
        private bool isLeftStepping;     // 현재 어느 발을 내디딜 차례인지 (교차 보행 관리)
        private bool isMoving;           // 현재 이동/회전 중인지 여부

        // Async Raycast Data
        private NativeArray<RaycastCommand> raycastCommands;
        private NativeArray<RaycastHit> raycastResults;
        private JobHandle raycastJobHandle;
        private Vector3 desiredPosL, desiredPosR; // 예측 발동 지점

        // 지면 정보를 담는 구조체 (Side-effect 방지용)
        private struct SurfaceData {
            public Vector3 point;
            public Vector3 normal;
            public bool isValid;
        }

        void OnEnable()
        {
            // 9개 레이: Body(0-2), FootL(3-5), FootR(6-8)
            raycastCommands = new NativeArray<RaycastCommand>(9, Allocator.Persistent);
            raycastResults = new NativeArray<RaycastHit>(9, Allocator.Persistent);
        }

        void OnDisable()
        {
            if (raycastCommands.IsCreated) raycastCommands.Dispose();
            if (raycastResults.IsCreated) raycastResults.Dispose();
        }

        void Start()
        {
            lastBodyPos = transform.position;
            currentFootPosL = footTargetL.position;
            currentFootPosR = footTargetR.position;
            
            // 초기 지면 높이에 맞춰 발 위치 고정 (동기방식 사용)
            currentFootPosL = GetGroundPointSync(transform.position + transform.right * -stepSpacing);
            currentFootPosR = GetGroundPointSync(transform.position + transform.right * stepSpacing);
            footTargetL.position = currentFootPosL;
            footTargetR.position = currentFootPosR;
        }

        void Update()
        {
            HandleMovement();

            // [Raycast Batch Scheduling]
            // 다음 프레임이나 LateUpdate에서 사용할 지면 정보를 미리 비동기로 요청합니다.
            PrepareRaycastBatch();
            raycastJobHandle = RaycastCommand.ScheduleBatch(raycastCommands, raycastResults, 1);
        }

        void LateUpdate()
        {
            // 비동기 레이캐스트 작업 완료 대기 (이미 완료되었을 가능성이 높음)
            raycastJobHandle.Complete();

            Vector3 myPos = transform.position;
            
            // [Surface Resolution] 배치 결과 해석
            SurfaceData bodySurface = ResolveSurfaceData(0); // Body Site index 0
            if (bodySurface.isValid) {
                targetNormal = bodySurface.normal;
            }

            // [Normal Smoothing]
            currentNormal = Vector3.Slerp(currentNormal, targetNormal, Time.deltaTime * normalSmoothSpeed);

            // [Surface Snapping] 
            if (bodySurface.isValid) {
                transform.position = Vector3.Lerp(myPos, bodySurface.point + currentNormal * bodyHeight, Time.deltaTime * 10f);
            }

            // [Gait & Foot Persistence]
            UpdateGait();

            if (!lStepping) footTargetL.position = currentFootPosL;
            if (!rStepping) footTargetR.position = currentFootPosR;
            
            ApplyBodySway();
        }

        /// <summary>
        /// WASD 입력을 받아 로봇의 이동 방향과 회전을 결정합니다.
        /// 전진 이동은 직접 transform을 수정하지 않고 velocity 변수에 의도만 저장합니다.
        /// </summary>
        void HandleMovement()
        {
            float horizontal = 0;
            float vertical = 0;

            // [New Input System] Keyboard 입력 처리
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed) vertical += 1;
                if (keyboard.sKey.isPressed) vertical -= 1;
                if (keyboard.aKey.isPressed) horizontal -= 1;
                if (keyboard.dKey.isPressed) horizontal += 1;
            }

            // [Move Direction Calculation]
            Vector3 myForward = transform.forward;
            Vector3 myRight = transform.right;
            Vector3 myUp = transform.up;

            bool moving = vertical != 0 || horizontal != 0;
            isMoving = moving;

            if (moving)
            {
                // W/S는 로컬 정면(forward), A/D는 로컬 측면(right) 이동 (Strafing)
                Vector3 combinedMove = (myForward * vertical + myRight * horizontal).normalized;
                Vector3 worldMoveDir = Vector3.ProjectOnPlane(combinedMove, currentNormal);
                
                if (worldMoveDir.sqrMagnitude > 0.0001f)
                {
                    velocity = worldMoveDir.normalized * moveSpeed;
                }
                else
                {
                    velocity = Vector3.zero;
                }
            }
            else
            {
                velocity = Vector3.zero;
            }

            // [Rotation Handling] 마우스 델타와 Q/E 키 입력을 결합
            float rotationInput = 0;
            
            // 마우스 델타 (New Input System)
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null) rotationInput += mouse.delta.x.ReadValue() * 0.1f;

            // 키보드 Q/E (New Input System)
            if (keyboard != null)
            {
                if (keyboard.qKey.isPressed) rotationInput -= 2f;
                if (keyboard.eKey.isPressed) rotationInput += 2f;
            }

            // 표면 법선 정렬: 현재 up을 지면 법선에 맞춤
            Quaternion alignmentRot = Quaternion.FromToRotation(myUp, currentNormal) * transform.rotation;
            
            // 회전 적용 (프레임 독립적으로 속도 계산)
            float finalRotation = rotationInput * turnSpeed * Time.deltaTime * 10f;
            transform.rotation = alignmentRot * Quaternion.Euler(0, finalRotation, 0);

            lastBodyPos = transform.position;
        }

        /// <summary>
        /// 보행 상태를 체크하고 발을 뗄 조건이 되면 발걸음 코루틴을 시작합니다.
        /// </summary>
        void UpdateGait()
        {
            if (IsStepping()) return;

            Vector3 restingPosL = transform.position + (transform.rotation * Vector3.right * -stepSpacing);
            Vector3 restingPosR = transform.position + (transform.rotation * Vector3.right * stepSpacing);

            // Stride Direction 예측 (Update에서 계산된 velocity 사용)
            Vector3 strideDir = velocity.magnitude > 0.1f ? velocity.normalized : Vector3.zero;
            desiredPosL = restingPosL + (strideDir * stepLength);
            desiredPosR = restingPosR + (strideDir * stepLength);

            float angleL = Vector3.Angle(transform.rotation * Vector3.left, (currentFootPosL - transform.position).normalized);
            float angleR = Vector3.Angle(transform.rotation * Vector3.right, (currentFootPosR - transform.position).normalized);

            // [Move Speed Sync]
            float dynamicDuration = stepDuration;
            float currentV = velocity.magnitude;
            if (currentV > 0.1f) 
            {
                dynamicDuration = (stepLength * 0.5f) / currentV;
                dynamicDuration = Mathf.Clamp(dynamicDuration, 0.01f, 2.0f);
            }
            else dynamicDuration *= 0.7f; 

            if (isLeftStepping)
            {
                SurfaceData footSurface = ResolveSurfaceData(1); // L Foot index 1
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleL > stepAngle;
                bool crossed = Vector3.Dot(transform.rotation * Vector3.right, (currentFootPosL - transform.position).normalized) > 0.15f;
                bool wayBehind = Vector3.Dot(transform.forward, (currentFootPosL - transform.position).normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    StartCoroutine(MoveFoot(true, footSurface.point, dynamicDuration));
                    isLeftStepping = false;
                }
            }
            else
            {
                SurfaceData footSurface = ResolveSurfaceData(2); // R Foot index 2
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleR > stepAngle;
                bool crossed = Vector3.Dot(transform.rotation * Vector3.left, (currentFootPosR - transform.position).normalized) > 0.15f;
                bool wayBehind = Vector3.Dot(transform.forward, (currentFootPosR - transform.position).normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    StartCoroutine(MoveFoot(false, footSurface.point, dynamicDuration));
                    isLeftStepping = true;
                }
            }
        }

        bool IsStepping() => lStepping || rStepping;
        
        private bool lStepping = false;
        private bool rStepping = false;

        /// <summary>
        /// 발을 들어올려 새 위치로 옮기는 애니메이션 코루틴입니다.
        /// 이 과정에서 몸체(Root)를 함께 전진시켜 지지발의 미끄러짐을 최소화합니다.
        /// </summary>
        IEnumerator MoveFoot(bool left, Vector3 targetFootPos, float duration)
        {
            if (left) { if (rStepping) yield break; lStepping = true; }
            else { if (lStepping) yield break; rStepping = true; }

            Vector3 startFootPos = left ? currentFootPosL : currentFootPosR;
            Vector3 startBodyPos = transform.position;
            
            // 몸체 이동 거리 계산 (한 스텝당 보폭의 절반만큼 몸체가 전진함)
            Vector3 bodyMoveOffset = Vector3.zero;
            if (velocity.magnitude > 0.1f) {
                bodyMoveOffset = velocity.normalized * (stepLength * 0.5f);
            }

            float elapsed = 0;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                float easedT = EaseInOutSine(t); // 움직임을 자연스럽게 만드는 가감속 처리
                
                // [Root Sync] 발이 공중에 떠 있는 동안 몸체를 같이 이동시킵니다.
                // 지면에 붙어 있는 Anchor 발은 월드 좌표계에서 그대로 유지되어 미끄러짐이 발생하지 않습니다.
                if (bodyMoveOffset != Vector3.zero) {
                    transform.position = Vector3.Lerp(startBodyPos, startBodyPos + bodyMoveOffset, easedT);
                }

                // 포물선 궤적으로 발의 위치 보간
                Vector3 currentPos = Vector3.Lerp(startFootPos, targetFootPos, easedT);
                currentPos.y += Mathf.Sin(t * Mathf.PI) * stepHeight;
                
                if (left) {
                    currentFootPosL = currentPos;
                    footTargetL.position = currentPos;
                } else {
                    currentFootPosR = currentPos;
                    footTargetR.position = currentPos;
                }
                
                yield return null;
            }

            // 오차 보정을 위한 최종 위치 강제 지정
            if (bodyMoveOffset != Vector3.zero) transform.position = startBodyPos + bodyMoveOffset;

            if (left) { 
                currentFootPosL = targetFootPos; 
                footTargetL.position = targetFootPos;
                lStepping = false; 
            } else { 
                currentFootPosR = targetFootPos; 
                footTargetR.position = targetFootPos;
                rStepping = false; 
            }
        }

        // 배치 데이터 해석 유틸리티
        private SurfaceData ResolveSurfaceData(int siteIndex)
        {
            int baseIdx = siteIndex * 3;
            RaycastHit floorHit = raycastResults[baseIdx];
            RaycastHit wallHit = raycastResults[baseIdx + 1];
            RaycastHit edgeHit = raycastResults[baseIdx + 2];

            bool hasFloor = floorHit.collider != null;
            bool hasWall = wallHit.collider != null && Vector3.Angle(transform.up, wallHit.normal) > 40f;
            bool hasEdge = edgeHit.collider != null;

            SurfaceData sd = new SurfaceData { isValid = true };
            Vector3 myPos = transform.position;

            if (hasWall)
            {
                float dWall = Vector3.Distance(myPos, wallHit.point);
                float dFloor = hasFloor ? Vector3.Distance(myPos, floorHit.point) : float.MaxValue;
                if (dWall < dFloor + 0.15f) { sd.point = wallHit.point; sd.normal = wallHit.normal; return sd; }
            }
            if (hasEdge) { sd.point = edgeHit.point; sd.normal = edgeHit.normal; return sd; }
            if (hasFloor) { sd.point = floorHit.point; sd.normal = floorHit.normal; return sd; }

            sd.isValid = false;
            sd.point = myPos;
            sd.normal = transform.up;
            return sd;
        }

        // 비동기 레이캐스트 명령 준비
        private void PrepareRaycastBatch()
        {
            Vector3 myUp = transform.up;
            Vector3 myForward = transform.forward;
            Vector3 moveDir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : myForward;
            float dynamicOffset = velocity.sqrMagnitude > 0.0001f ? detectionOffset : 0f;

            // Site 0: Body, Site 1: FootL, Site 2: FootR
            SetupProbeCommands(0, transform.position, myUp, moveDir, dynamicOffset);
            SetupProbeCommands(1, desiredPosL, myUp, moveDir, dynamicOffset);
            SetupProbeCommands(2, desiredPosR, myUp, moveDir, dynamicOffset);
        }

        private void SetupProbeCommands(int siteIndex, Vector3 pos, Vector3 up, Vector3 moveDir, float offset)
        {
            int baseIdx = siteIndex * 3;
            int mask = groundLayer.value;

            // 1. Floor
            Vector3 floorOrigin = pos + (up * 1f) + (moveDir * offset);
            raycastCommands[baseIdx] = new RaycastCommand(floorOrigin, -up, 3f, mask);

            // 2. Wall
            float wallDist = detectionOffset * 1.5f;
            raycastCommands[baseIdx + 1] = new RaycastCommand(pos, moveDir, wallDist, mask);

            // 3. Edge
            Vector3 probeOrigin = pos + moveDir * (detectionOffset * 1.2f) + up * -1.0f;
            raycastCommands[baseIdx + 2] = new RaycastCommand(probeOrigin, -moveDir, detectionOffset * 1.5f, mask);
        }

        // 초기화 시에만 사용하는 동기식 감지
        private Vector3 GetGroundPointSync(Vector3 samplePos)
        {
            if (Physics.Raycast(samplePos + transform.up, -transform.up, out RaycastHit hit, 3f, groundLayer))
                return hit.point;
            return samplePos;
        }

        // 기존 GetGroundPos를 동기식 레거시로 유지 (필요 시)
        private SurfaceData GetGroundPos(Vector3 pos) => ResolveSurfaceData(0); 

        // 사인 곡선을 이용한 입출력 가속 보간
        float EaseInOutSine(float x) => -(Mathf.Cos(Mathf.PI * x) - 1) / 2;

        private Vector3 currentNormal = Vector3.up;
        private Vector3 targetNormal = Vector3.up;

        void ApplyBodySway()
        {
            // 몸체의 좌우 흔들림(Sway)이나 상하 통통 튀는 움직임을 추가할 수 있는 공간입니다.
        }
    }
}
