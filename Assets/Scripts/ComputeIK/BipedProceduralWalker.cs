using UnityEngine;
using System.Collections;

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

        void Start()
        {
            lastBodyPos = transform.position;
            currentFootPosL = footTargetL.position;
            currentFootPosR = footTargetR.position;
            
            // 초기 지면 높이에 맞춰 발 위치 고정
            currentFootPosL = GetGroundPos(transform.position + transform.right * -stepSpacing);
            currentFootPosR = GetGroundPos(transform.position + transform.right * stepSpacing);
            footTargetL.position = currentFootPosL;
            footTargetR.position = currentFootPosR;
        }

        void Update()
        {
            HandleMovement(); // 입력 처리 및 회전 제어
            UpdateGait();      // 보행 리듬 및 발걸음 트리거 업데이트
        }

        void LateUpdate()
        {
            // [Zero-Slip] 움직이지 않는 발은 월드 좌표에 완전히 고정시켜 미끄러짐을 방지합니다.
            // IK 엔진이 매 프레임 이 타겟의 위치를 기준으로 관절을 해결합니다.
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

            // 새로운 Input System 호환성 유지
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed) vertical += 1;
                if (keyboard.sKey.isPressed) vertical -= 1;
                if (keyboard.aKey.isPressed) horizontal -= 1;
                if (keyboard.dKey.isPressed) horizontal += 1;
            }

            Vector3 moveDir = new Vector3(horizontal, 0, vertical).normalized;
            
            // 이동 방향과 이동 속도를 velocity에 저장하여 Gait 로직에서 사용하도록 합니다.
            velocity = moveDir * moveSpeed;

            if (moveDir.magnitude > 0.1f)
            {
                // 몸체를 가려는 방향으로 부드럽게 회전 (Always responsive)
                Quaternion targetRot = Quaternion.LookRotation(moveDir);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
                isMoving = true;
            }
            else
            {
                isMoving = false;
            }

            lastBodyPos = transform.position;
        }

        /// <summary>
        /// 보행 상태를 체크하고 발을 뗄 조건이 되면 발걸음 코루틴을 시작합니다.
        /// </summary>
        void UpdateGait()
        {
            if (IsStepping()) return; // 이미 발을 움직이는 중이면 중복 실행 방지

            // 발이 기본적으로 위치해야 할 '가상의 휴식 위치' 계산
            Vector3 restingPosL = transform.position + (transform.rotation * Vector3.right * -stepSpacing);
            Vector3 restingPosR = transform.position + (transform.rotation * Vector3.right * stepSpacing);

            // 보간을 통해 발이 착지해야 할 먼 미래의 목표 지점 예측
            Vector3 moveDir = velocity.magnitude > 0.1f ? velocity.normalized : transform.forward;
            Vector3 desiredPosL = restingPosL + (moveDir * stepLength);
            Vector3 desiredPosR = restingPosR + (moveDir * stepLength);

            // 몸체 회전으로 인해 발이 벌어지는 각도 측정
            float angleL = Vector3.Angle(transform.rotation * Vector3.left, (currentFootPosL - transform.position).normalized);
            float angleR = Vector3.Angle(transform.rotation * Vector3.right, (currentFootPosR - transform.position).normalized);

            // 제자리 회전 시에는 발걸음을 30% 더 빠르게 하여 민첩하게 셔플하도록 설정
            float dynamicDuration = stepDuration;
            if (velocity.magnitude < 0.1f) dynamicDuration *= 0.7f; 

            if (isLeftStepping) // 왼발이 움직일 차례
            {
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleL > stepAngle;
                // 발이 몸체 안쪽으로 꼬였는지(Crossing) 감지
                bool crossed = Vector3.Dot(transform.rotation * Vector3.right, (currentFootPosL - transform.position).normalized) > 0.15f;
                // 몸체가 갑자기 돌아가서 발이 등 뒤에 남겨졌는지 감지
                bool wayBehind = Vector3.Dot(transform.forward, (currentFootPosL - transform.position).normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    StartCoroutine(MoveFoot(true, GetGroundPos(desiredPosL), dynamicDuration));
                    isLeftStepping = false;
                }
            }
            else // 오른발이 움직일 차례
            {
                bool wantToMove = velocity.magnitude > 0.1f;
                bool tooRotated = angleR > stepAngle;
                bool crossed = Vector3.Dot(transform.rotation * Vector3.left, (currentFootPosR - transform.position).normalized) > 0.15f;
                bool wayBehind = Vector3.Dot(transform.forward, (currentFootPosR - transform.position).normalized) < -0.4f;

                if (wantToMove || tooRotated || crossed || wayBehind)
                {
                    StartCoroutine(MoveFoot(false, GetGroundPos(desiredPosR), dynamicDuration));
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
                
                // 급회전(40도 이상) 중에는 몸체를 전진시키지 않고 제자리에서 발만 셔플하도록 잠금
                if (Vector3.Angle(transform.forward, velocity.normalized) > 40f) {
                    bodyMoveOffset = Vector3.zero;
                }
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

        // 사인 곡선을 이용한 입출력 가속 보간
        float EaseInOutSine(float x) => -(Mathf.Cos(Mathf.PI * x) - 1) / 2;

        /// <summary>
        /// 위치를 기준으로 지면을 수직 레이케스트하여 충돌한 지점의 월드 좌표를 반환합니다.
        /// </summary>
        Vector3 GetGroundPos(Vector3 pos)
        {
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out hit, 10f, groundLayer))
            {
                return hit.point;
            }
            return new Vector3(pos.x, 0, pos.z);
        }

        void ApplyBodySway()
        {
            // 몸체의 좌우 흔들림(Sway)이나 상하 통통 튀는 움직임을 추가할 수 있는 공간입니다.
        }
    }
}
