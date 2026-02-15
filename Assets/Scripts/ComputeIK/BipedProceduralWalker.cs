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

        [Header("Predictive Steering")]
        public int predictionSteps = 3;   // 미래 경로를 예측할 노드 수 (높을수록 부드럽지만 반응이 느려짐)
        public float predictionStepDist = 0.2f; // 예측 노드 간의 간격 (미터)

        private Vector3 lastBodyPos;
        private Vector3 velocity;         // 이동 '의도'를 담는 속도 벡터
        
        private Vector3 currentFootPosL;   // 왼쪽 발의 현재 월드 위치
        private Vector3 currentFootPosR;   // 오른쪽 발의 현재 월드 위치
        private Vector3 targetFootPosL;
        private Vector3 targetFootPosR;
        
        private bool isLeftStepping;     // 현재 어느 발을 내디딜 차례인지 (교차 보행 관리)
        private bool isMoving;           // 현재 이동/회전 중인지 여부
        private float rotationInput;      // 수집된 회전 입력값

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
            // Raycast Batch 크기: Body 예측(predictionSteps * 3) + FootL(3) + FootR(3)
            int totalRays = (predictionSteps * 3) + 6;
            raycastCommands = new NativeArray<RaycastCommand>(totalRays, Allocator.Persistent);
            raycastResults = new NativeArray<RaycastHit>(totalRays, Allocator.Persistent);
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
            UpdateMovementIntent();
            PrepareRaycastBatch();
            raycastJobHandle = RaycastCommand.ScheduleBatch(raycastCommands, raycastResults, 1);
        }

        void LateUpdate()
        {
            raycastJobHandle.Complete();
            SynchPhysicsWithGait();
        }

        public Transform referenceCamera; // [New] Camera reference for relative movement

        /// <summary>
        /// 사용자 입력을 처리하고 이동 의도(Velocity)와 발 착지 예상 지점을 계산합니다.
        /// </summary>
        private void UpdateMovementIntent()
        {
            if (referenceCamera == null)
            {
                if (Camera.main != null) referenceCamera = Camera.main.transform;
                else return; 
            }

            // 1. 입력 수집 (Input Gathering)
            float moveX = 0;      // A/D
            float moveZ = 0;      // W/S
            float strafeInput = 0; // Q/E
            
            rotationInput = 0; 

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed) moveZ += 1;
                if (keyboard.sKey.isPressed) moveZ -= 1;
                if (keyboard.aKey.isPressed) moveX -= 1;
                if (keyboard.dKey.isPressed) moveX += 1;
                
                if (keyboard.qKey.isPressed) strafeInput -= 1;
                if (keyboard.eKey.isPressed) strafeInput += 1;
            }

            // 2. 통합 이동 처리 (Optimized Movement Logic)
            HandleMovementCameraRelative(moveX, moveZ, strafeInput);

            // 3. 발걸음 감지용 목표 지점 미리 계산
            Vector3 myPos = transform.position;
            Quaternion myRot = transform.rotation;
            Vector3 strideDir = velocity.magnitude > 0.1f ? velocity.normalized : Vector3.zero;
            
            desiredPosL = myPos + (myRot * Vector3.right * -stepSpacing) + (strideDir * stepLength);
            desiredPosR = myPos + (myRot * Vector3.right * stepSpacing) + (strideDir * stepLength);
        }

        /// <summary>
        /// 카메라 기준 입력 처리 (최적화 버전)
        /// </summary>
        void HandleMovementCameraRelative(float moveX, float moveZ, float strafeInput)
        {
            // [Basis Calculation]
            // Build a robust Orthogonal Basis (Right, Forward) on the current surface.
            Vector3 surfRight = Vector3.ProjectOnPlane(referenceCamera.right, currentNormal).normalized;
            Vector3 surfFwd;

            // Singularity Check: If Camera Right aligns with Normal (e.g., wall run), projection fails.
            if (surfRight.sqrMagnitude < 0.001f)
            {
                // Fallback: Project Forward vector first
                surfFwd = Vector3.ProjectOnPlane(referenceCamera.forward, currentNormal).normalized;
                // Reconstruct Right from Forward x Normal
                surfRight = Vector3.Cross(currentNormal, surfFwd).normalized;
            }
            else
            {
                // Standard: Derive Forward from Right x Normal
                // This guarantees 'Screen Up' maps to 'Surface Upward' (Fixes inverted vertical movement)
                surfFwd = Vector3.Cross(surfRight, currentNormal).normalized;
            }

            // [Input Combination]
            // Merge lateral inputs (MoveX + Strafe) before vector math
            float finalX = moveX + strafeInput;
            
            Vector3 targetMoveDir = (surfFwd * moveZ) + (surfRight * finalX);
            
            if (targetMoveDir.sqrMagnitude > 1f) targetMoveDir.Normalize();
            
            isMoving = targetMoveDir.sqrMagnitude > 0.0001f;
            velocity = isMoving ? targetMoveDir * moveSpeed : Vector3.zero;

            // [Rotation Logic]
            // Strafe (Q/E) -> Look at Camera Forward (Surface projected)
            // Move (WASD) -> Look at Movement Direction
            if (Mathf.Abs(strafeInput) > 0.01f)
            {
                RotateTowards(surfFwd);
            }
            else if (isMoving)
            {
                RotateTowards(targetMoveDir);
            }
            
            lastBodyPos = transform.position;
        }

        private void RotateTowards(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            
            Quaternion targetRot = Quaternion.LookRotation(dir, currentNormal);
            // SynchPhysicsWithGait에서 덮어쓰지 않도록 'rotationInput' 의존성을 줄이거나,
            // 여기서 직접 보간 회전을 수행함. 
            // 단, SynchPhysicsWithGait의 161라인(alignment)과 충돌 가능성 있음.
            
            // 해결책: SynchPhysicsWithGait에서는 지면 경사(Alignment)만 맞추고, Y축 회전은 여기서 결정된 값을 사용하도록 변경 필요.
            // 일단 부드럽게 보간
            float step = turnSpeed * Time.deltaTime * 5f;
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, step);
            
            // SynchPhysicsWithGait의 rotationInput 영향을 상쇄하기 위해 0으로 유지 (위에서 초기화함)
        }

        /// <summary>
        /// 물리적 위치(지면 스냅)와 보행 애니메이션(발 고정)을 동기화합니다.
        /// </summary>
        private void SynchPhysicsWithGait()
        {
            Vector3 myPos = transform.position;
            Vector3 myUp = transform.up;
            
            // [1. Body Continuous Movement]
            Vector3 targetBodyPos = myPos + velocity * Time.deltaTime;

            // [2. Predictive Surface Resolution]
            SurfaceData predictiveSurface = ResolvePredictiveSurface(0, myPos);
            if (predictiveSurface.isValid) {
                targetNormal = predictiveSurface.normal;
                targetBodyPos = predictiveSurface.point + targetNormal * bodyHeight;
            }

            // [3. Smoothing & Rotation]
            currentNormal = Vector3.Slerp(currentNormal, targetNormal, Time.deltaTime * normalSmoothSpeed);
            
            // [Modified] Rotation is now handled by UpdateMovementIntent -> RotateTowards.
            // We only align the Up-vector to the terrain normal here.
            Quaternion alignmentRot = Quaternion.FromToRotation(transform.up, currentNormal) * transform.rotation;
            transform.rotation = alignmentRot;

            // [4. Apply Position]
            if (predictiveSurface.isValid) {
                if (Vector3.Distance(myPos, targetBodyPos) > 0.45f) transform.position = targetBodyPos;
                else transform.position = Vector3.Lerp(myPos, targetBodyPos, Time.deltaTime * 20f);
            } else {
                transform.position = Vector3.Lerp(myPos, targetBodyPos, Time.deltaTime * 20f);
            }

            // [5. Gait & Zero-Slip]
            UpdateGait();

            if (!lStepping) footTargetL.position = currentFootPosL;
            if (!rStepping) footTargetR.position = currentFootPosR;
            
            ApplyBodySway();
        }

        /// <summary>
        /// 보행 상태를 체크하고 발을 뗄 조건이 되면 발걸음 코루틴을 시작합니다.
        /// </summary>
        void UpdateGait()
        {
            if (IsStepping()) return;

            // [Modified] 각도 계산 시 캐릭터의 로컬 평면에 투영하여 판단 (벽 타기 대응)
            Vector3 toFootL = currentFootPosL - transform.position;
            Vector3 toFootR = currentFootPosR - transform.position;
            Vector3 toFootLFlat = Vector3.ProjectOnPlane(toFootL, transform.up);
            Vector3 toFootRFlat = Vector3.ProjectOnPlane(toFootR, transform.up);

            float angleL = Vector3.Angle(transform.rotation * Vector3.left, toFootLFlat.normalized);
            float angleR = Vector3.Angle(transform.rotation * Vector3.right, toFootRFlat.normalized);

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
                // 몸체 예측 노드 이후의 index가 발 위치 데이터임
                SurfaceData footSurface = ResolvePredictiveSurface(predictionSteps, desiredPosL);
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleL > stepAngle;
                
                // [Modified] crossed 계산 시 캐릭터의 로컬 평면에 투영하여 판단 (벽 타기 대응)
                bool crossed = Vector3.Dot(transform.rotation * Vector3.right, toFootLFlat.normalized) > 0.15f;
                
                // [Modified] wayBehind 계산 시 캐릭터의 로컬 평면에 투영하여 판단 (벽 타기 대응)
                bool wayBehind = Vector3.Dot(transform.forward, toFootLFlat.normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    // Debug.Log($"[Left Step] Move:{wantToMove}, Rot:{tooRotated}({angleL:F1}), Cross:{crossed}, Behind:{wayBehind}");
                    StartCoroutine(MoveFoot(true, footSurface.point, dynamicDuration));
                    isLeftStepping = false;
                }
            }
            else
            {
                SurfaceData footSurface = ResolvePredictiveSurface(predictionSteps + 1, desiredPosR);
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleR > stepAngle;
                
                // [Modified] crossed 계산 시 캐릭터의 로컬 평면에 투영하여 판단 (벽 타기 대응)
                bool crossed = Vector3.Dot(transform.rotation * Vector3.left, toFootRFlat.normalized) > 0.15f;
                
                // [Modified] wayBehind 계산 시 캐릭터의 로컬 평면에 투영하여 판단 (벽 타기 대응)
                bool wayBehind = Vector3.Dot(transform.forward, toFootRFlat.normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    // Debug.Log($"[Right Step] Move:{wantToMove}, Rot:{tooRotated}({angleR:F1}), Cross:{crossed}, Behind:{wayBehind}");
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
            if (left) lStepping = true;
            else rStepping = true;

            Vector3 startFootPos = left ? currentFootPosL : currentFootPosR;
            float elapsed = 0;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float easedT = EaseInOutSine(t);

                // [Foot Trajectory] 포물선 궤적으로 발 이동
                Vector3 footPos = Vector3.Lerp(startFootPos, targetFootPos, easedT);
                footPos += currentNormal * Mathf.Sin(t * Mathf.PI) * stepHeight;
                
                if (left) { 
                    currentFootPosL = footPos; 
                    footTargetL.position = footPos;
                } else { 
                    currentFootPosR = footPos; 
                    footTargetR.position = footPos;
                }

                yield return null;
            }

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

        // N-Step 예측 지형 해석 유틸리티 (가중치 평균 법선 산출)
        private SurfaceData ResolvePredictiveSurface(int startSiteIndex, Vector3 currentPos)
        {
            raycastJobHandle.Complete();

            Vector3 blendedNormal = Vector3.zero;
            Vector3 averagePoint = currentPos;
            bool anyValid = false;
            float totalWeight = 0;

            // 각 예측 노드의 데이터를 순회하며 가중치 블렌딩
            for (int i = 0; i < predictionSteps; i++)
            {
                SurfaceData stepSD = ResolveSurfaceData(startSiteIndex + i, currentPos);
                if (stepSD.isValid)
                {
                    // 현재 위치와 가까울수록 높은 가중치 (1.0 -> 0.2 등)
                    float weight = 1.0f / (i + 1);
                    
                    // 만약 미래 노드에서 벽(Wall)이 감지되면 가중치를 대폭 높여 미리 대비함
                    if (Vector3.Angle(Vector3.up, stepSD.normal) > 45f) weight *= 2.0f;
                    
                    blendedNormal += stepSD.normal * weight;
                    if (i == 0) averagePoint = stepSD.point; // 위치는 현재 지점 위주로
                    
                    totalWeight += weight;
                    anyValid = true;
                }
            }

            SurfaceData res = new SurfaceData { isValid = anyValid };
            if (anyValid)
            {
                res.normal = (blendedNormal / totalWeight).normalized;
                res.point = averagePoint;
            }
            else
            {
                res.normal = transform.up;
                res.point = currentPos;
            }
            return res;
        }

        private SurfaceData ResolveSurfaceData(int siteIndex, Vector3 siteOrigin)
        {
            // 비동기 작업 완료 보장 (코루틴 등에서 호출 시)
            raycastJobHandle.Complete();
            
            int baseIdx = siteIndex * 3;
            if (baseIdx + 2 >= raycastResults.Length) return new SurfaceData { isValid = false };
            RaycastHit floorHit = raycastResults[baseIdx];
            RaycastHit wallHit = raycastResults[baseIdx + 1];
            RaycastHit edgeHit = raycastResults[baseIdx + 2];

            bool hasFloor = floorHit.collider != null;
            // 캐릭터의 회전 상태가 아닌, 실제 지면들 사이의 각도차이를 계산하여 전환 중의 Jitter 방지
            bool hasWall = wallHit.collider != null && (!hasFloor || Vector3.Angle(floorHit.normal, wallHit.normal) > 35f);
            bool hasEdge = edgeHit.collider != null;

            SurfaceData sd = new SurfaceData { isValid = true };
            Vector3 myPos = transform.position;

            if (hasWall)
            {
                float dWall = Vector3.Distance(siteOrigin, wallHit.point);
                float dFloor = hasFloor ? Vector3.Distance(siteOrigin, floorHit.point) : float.MaxValue;
                
                // [Transition Bias] 벽이 충분히 근처에 있다면(0.35f) 바닥보다 우선순위를 높임
                if (dWall < dFloor + 0.35f) 
                { 
                    sd.point = wallHit.point; 
                    sd.normal = wallHit.normal; 
                    return sd; 
                }
            }
            if (hasEdge) { sd.point = edgeHit.point; sd.normal = edgeHit.normal; return sd; }
            if (hasFloor) { sd.point = floorHit.point; sd.normal = floorHit.normal; return sd; }

            sd.isValid = false;
            sd.point = myPos;
            sd.normal = transform.up;
            return sd;
        }

        // 비동기 레이캐스트 명령 준비 (N-Step 경로 예측 포함)
        private void PrepareRaycastBatch()
        {
            Vector3 refUp = currentNormal;
            Vector3 myForward = transform.forward;
            Vector3 moveDir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : myForward;
            float dynamicOffset = velocity.sqrMagnitude > 0.0001f ? detectionOffset : 0f;

            // 1. 몸체 경로 예측 노드들 (Site 0 ~ N-1)
            for (int i = 0; i < predictionSteps; i++)
            {
                Vector3 predictPos = transform.position + (moveDir * i * predictionStepDist);
                SetupProbeCommands(i, predictPos, refUp, moveDir, dynamicOffset);
            }

            // 2. 발 목표 지점들 (Body 이후 index 사용)
            int footL_Idx = predictionSteps;
            int footR_Idx = predictionSteps + 1;
            SetupProbeCommands(footL_Idx, desiredPosL, refUp, moveDir, dynamicOffset);
            SetupProbeCommands(footR_Idx, desiredPosR, refUp, moveDir, dynamicOffset);
        }

        // [Debug Visualization]
        void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            // 1. 예측 경로 (Predictive Path)
            Gizmos.color = Color.cyan;
            Vector3 refUp = currentNormal;
            Vector3 moveDir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : transform.forward;
            
            // 비동기 작업 중이 아닐 때만 정확한 비주얼이 가능 (Job이 완료된 시점)
            if (raycastJobHandle.IsCompleted)
            {
                // Body Nodes
                for (int i = 0; i < predictionSteps; i++)
                {
                    SurfaceData sd = ResolveSurfaceData(i, Vector3.zero); // Origin은 Debug용이라 크게 중요치 않음
                    if (sd.isValid)
                    {
                        // 벽이면 빨간색, 평지면 초록색
                        bool isWall = Vector3.Angle(Vector3.up, sd.normal) > 45f;
                        Gizmos.color = isWall ? Color.red : Color.green;
                        Gizmos.DrawWireSphere(sd.point, 0.1f);
                        Gizmos.DrawLine(sd.point, sd.point + sd.normal * 0.5f);
                    }
                    else
                    {
                        // Raycast 실패 시 예측 위치만 표시
                        Gizmos.color = Color.gray;
                        Vector3 pPos = transform.position + (moveDir * i * predictionStepDist);
                        Gizmos.DrawWireSphere(pPos, 0.05f);
                    }
                }
            }

            // 2. 발 IK 타겟
            Gizmos.color = Color.blue;
            Gizmos.DrawWireCube(footTargetL.position, Vector3.one * 0.1f);
            Gizmos.DrawWireCube(footTargetR.position, Vector3.one * 0.1f);
        }

        private void SetupProbeCommands(int siteIndex, Vector3 pos, Vector3 up, Vector3 moveDir, float offset)
        {
            int baseIdx = siteIndex * 3;
            int mask = groundLayer.value;

            // 1. Floor
            Vector3 floorOrigin = pos + (up * 1f) + (moveDir * offset);
            raycastCommands[baseIdx] = new RaycastCommand(floorOrigin, -up, 3f, mask);

            // 2. Wall (진행 방향 앞쪽 감지)
            // 검사 거리를 충분히 확보하여 고속 주행 시에도 벽을 미리 감지 (1.5m)
            float wallDist = 1.5f;
            // 레이 발사 지점을 약간 뒤(-0.15f)와 위(+0.15f)에서 시작하여 이미 근접한 벽도 놓치지 않게 함
            Vector3 wallOrigin = pos + up * 0.15f - moveDir * 0.15f;
            raycastCommands[baseIdx + 1] = new RaycastCommand(wallOrigin, moveDir, wallDist, mask);

            // 3. Edge (낭떠러지/외각 모서리 감지)
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
        private SurfaceData GetGroundPos(Vector3 pos) => ResolveSurfaceData(0, pos); 

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
