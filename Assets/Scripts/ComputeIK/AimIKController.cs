using UnityEngine;

namespace ComputeIK
{
    /// <summary>
    /// 무기가 특정 목표를 조준하도록 IK Target과 Pole의 위치를 제어하고,
    /// 최종적으로 총구(Muzzle) 방향이 목표를 향하도록 손(Hand)의 회전값을 보정하는 컨트롤러입니다.
    /// </summary>
    [System.Serializable]
    public class AimArm
    {
        [Tooltip("실제로 바라보고자 하는 독립적인 목표 (예: 각각의 레이캐스트 된 위치)")]
        public Transform aimTarget;

        [Tooltip("ComputeIK의 대상 Target")]
        public Transform ikTarget;
        [Tooltip("ComputeIK의 대상 Pole")]
        public Transform ikPole;
        [Tooltip("목표를 향하는 기준점이 될 어깨(Shoulder) 본")]
        public Transform shoulderBone;
        [Tooltip("IK 완료 후 회전을 보정할 손(Hand) 본")]
        public Transform handBone;
        [Tooltip("실제 총알이 나가는 무기의 총구 트랜스폼. 이 축의 Forward(Z)가 타겟을 향하게 됩니다.")]
        public Transform weaponMuzzle;

        // --- 내부 상태 (컴파일/에디터에 보일 필요 없음) ---
        [System.NonSerialized] public float outOfViewTimer = 0f;
        [System.NonSerialized] public float aimWeight = 0f;
        [System.NonSerialized] public Vector3 defaultTargetOffset;
        [System.NonSerialized] public Vector3 defaultPoleOffset;
        [System.NonSerialized] public Quaternion defaultMuzzleRotation;
        [System.NonSerialized] public Quaternion muzzleOffset; // [추가] 표준 LookRotation과 실제 무기 방향 사이의 차이
        [System.NonSerialized] public bool initialized = false;
    }

    public class AimIKController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("캐릭터의 정면 방향을 알기 위한 기준(Root 또는 Chest) 트랜스폼")]
        public Transform characterBody;

        [Header("Arms to Aim")]
        [Tooltip("조준에 사용할 팔들의 배열 (양손 지원)")]
        public AimArm[] arms;

        [Header("Aim Restrictions")]
        [Tooltip("캐릭터 정면을 기준으로 총구가 허용하는 최대 조준 각도")]
        [Range(0, 180)]
        public float maxAimAngle = 70f;
        [Tooltip("IK Target을 어깨로부터 띄워둘 거리 (팔이 완전히 뻗어지지 않고 자연스럽게 굽어지도록 설정)")]
        public float targetDistance = 1.0f;

        [Header("Pole Settings")]
        [Tooltip("어깨 좌표 기준으로 팔꿈치가 기본적으로 향하게 할 로컬 오프셋")]
        public Vector3 poleLocalOffset = new Vector3(0, -1f, -0.5f);

        [Header("Idle & Return Settings")]
        [Tooltip("타겟에서 벗어났을 때 대상이 없거나 시야각 밖일 경우 대기 상태로 돌아가기까지의 지연 시간")]
        public float idleDelay = 1.0f;
        [Tooltip("대기 자세로 돌아가고 총구를 내리는 속도")]
        public float returnSpeed = 5.0f;

        void Start()
        {
            if (arms == null || characterBody == null) return;

            foreach (var arm in arms)
            {
                InitializeArm(arm);
            }
        }

        private void InitializeArm(AimArm arm)
        {
            if (arm.ikTarget != null)
            {
                arm.defaultTargetOffset = characterBody.InverseTransformPoint(arm.ikTarget.position);
            }
            if (arm.ikPole != null)
            {
                arm.defaultPoleOffset = characterBody.InverseTransformPoint(arm.ikPole.position);
            }
            
            Transform muzzle = GetMuzzle(arm);
            if (muzzle != null && arm.handBone != null)
            {
                // 1. 손(Hand)과 총구(Muzzle) 사이의 고정된 상대 회전 기억
                arm.defaultMuzzleRotation = Quaternion.Inverse(arm.handBone.rotation) * muzzle.rotation;

                // 2. [개선] 무기 모델의 축 방향(X, Y, Z)을 자동 탐지하여 오프셋 저장
                // 캐릭터 정면/상단이 모델의 어떤 로컬 축과 가장 일치하는지 찾습니다.
                Vector3 localFwd = muzzle.InverseTransformDirection(characterBody.forward);
                Vector3 localUp = muzzle.InverseTransformDirection(characterBody.up);
                
                Vector3 snapFwd = GetClosestPrincipalAxis(localFwd);
                Vector3 snapUp = GetClosestPrincipalAxis(localUp);
                // 만약 snapUp이 forward와 겹치면 직교하는 다른 축을 선택
                if (Mathf.Abs(Vector3.Dot(snapFwd, snapUp)) > 0.8f) 
                    snapUp = (Mathf.Abs(snapFwd.y) > 0.5f) ? Vector3.forward : Vector3.up;

                // 이 오프셋은 '표준 LookRotation(Z-Forward)'에서 '실제 모델 축'으로 가는 회전입니다.
                arm.muzzleOffset = Quaternion.Inverse(Quaternion.LookRotation(snapFwd, snapUp));
            }

            arm.aimWeight = 0f;
            arm.initialized = true;
        }

        void Update()
        {
            if (characterBody == null || arms == null)
                return;

            foreach (var arm in arms)
            {
                if (!arm.initialized || arm.shoulderBone == null || arm.ikTarget == null)
                    continue;

                bool hasValidTarget = (arm.aimTarget != null);

                if (hasValidTarget)
                {
                    Vector3 directionToTarget = arm.aimTarget.position - arm.shoulderBone.position;
                    float currentAngle = Vector3.Angle(characterBody.forward, directionToTarget);

                    if (currentAngle > maxAimAngle)
                    {
                        hasValidTarget = false;
                    }
                }

                if (hasValidTarget)
                {
                    arm.outOfViewTimer = 0f;
                    arm.aimWeight = Mathf.Lerp(arm.aimWeight, 1f, Time.deltaTime * returnSpeed);
                }
                else
                {
                    arm.outOfViewTimer += Time.deltaTime;
                    if (arm.outOfViewTimer > idleDelay)
                    {
                        arm.aimWeight = Mathf.Lerp(arm.aimWeight, 0f, Time.deltaTime * returnSpeed);
                    }
                }

                Vector3 idleTargetPos = characterBody.TransformPoint(arm.defaultTargetOffset);
                Vector3 activeTargetPos = idleTargetPos;

                if (arm.aimTarget != null)
                {
                    Vector3 rawDir = arm.aimTarget.position - arm.shoulderBone.position;
                    Vector3 clampedDirection = rawDir.normalized;
                    
                    if (Vector3.Angle(characterBody.forward, rawDir) > maxAimAngle)
                    {
                        clampedDirection = Vector3.RotateTowards(characterBody.forward, rawDir.normalized, maxAimAngle * Mathf.Deg2Rad, 0f);
                    }
                    
                    activeTargetPos = arm.shoulderBone.position + clampedDirection * targetDistance;
                }

                arm.ikTarget.position = Vector3.Lerp(idleTargetPos, activeTargetPos, arm.aimWeight);

                if (arm.ikPole != null)
                {
                    Vector3 idlePolePos = characterBody.TransformPoint(arm.defaultPoleOffset);
                    Vector3 activePolePos = arm.shoulderBone.position + (characterBody.rotation * poleLocalOffset);

                    arm.ikPole.position = Vector3.Lerp(idlePolePos, activePolePos, arm.aimWeight);
                }
            }
        }

        void LateUpdate()
        {
            if (arms == null || characterBody == null)
                return;

            foreach (var arm in arms)
            {
                Transform muzzle = GetMuzzle(arm);
                
                if (!arm.initialized || arm.handBone == null || muzzle == null || arm.ikTarget == null || arm.shoulderBone == null)
                    continue;

                if (arm.aimWeight <= 0.01f)
                    continue;

                Vector3 targetDir = (arm.ikTarget.position - arm.shoulderBone.position).normalized;
                
                if (arm.aimTarget != null)
                {
                    float distToTarget = Vector3.Distance(muzzle.position, arm.aimTarget.position);
                    if (distToTarget > 1.5f)
                    {
                        Vector3 realDir = (arm.aimTarget.position - muzzle.position).normalized;
                        if (Vector3.Angle(targetDir, realDir) < 30f)
                        {
                            targetDir = realDir;
                        }
                    }
                }
                
                if (targetDir != Vector3.zero)
                {
                    // [핵심] LookRotation으로 Z축을 타겟에, Y축을 캐릭터 Up에 고정 (뒤집힘 방지)
                    Quaternion desiredStandardRot = Quaternion.LookRotation(targetDir, characterBody.up);
                    
                    // 저장해둔 무기 모델의 축 오프셋을 적용하여 실제 조준 회전값 산출
                    Quaternion desiredMuzzleRot = desiredStandardRot * arm.muzzleOffset;
                    
                    // 현재 Muzzle과의 차이를 구해 HandBone에 적용
                    Quaternion correction = desiredMuzzleRot * Quaternion.Inverse(muzzle.rotation);
                    
                    Quaternion finalCorrection = Quaternion.Slerp(Quaternion.identity, correction, arm.aimWeight);
                    arm.handBone.rotation = finalCorrection * arm.handBone.rotation;
                }
            }
        }

        private Transform GetMuzzle(AimArm arm)
        {
            if (arm.weaponMuzzle != null) return arm.weaponMuzzle;

            if (arm.handBone != null)
            {
                Transform found = FindDeepChild(arm.handBone, "nozzle");
                if (found != null)
                {
                    arm.weaponMuzzle = found;
                    
                    if (arm.defaultMuzzleRotation == Quaternion.identity)
                    {
                        arm.defaultMuzzleRotation = Quaternion.Inverse(arm.handBone.rotation) * found.rotation;
                        
                        Vector3 localFwd = found.InverseTransformDirection(characterBody.forward);
                        Vector3 localUp = found.InverseTransformDirection(characterBody.up);
                        Vector3 snapFwd = GetClosestPrincipalAxis(localFwd);
                        Vector3 snapUp = GetClosestPrincipalAxis(localUp);
                        if (Mathf.Abs(Vector3.Dot(snapFwd, snapUp)) > 0.8f) 
                            snapUp = (Mathf.Abs(snapFwd.y) > 0.5f) ? Vector3.forward : Vector3.up;

                        arm.muzzleOffset = Quaternion.Inverse(Quaternion.LookRotation(snapFwd, snapUp));
                    }

                    return found;
                }
            }

            return arm.weaponMuzzle;
        }

        private Vector3 GetClosestPrincipalAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x);
            float y = Mathf.Abs(v.y);
            float z = Mathf.Abs(v.z);
            if (x > y && x > z) return new Vector3(Mathf.Sign(v.x), 0, 0);
            if (y > x && y > z) return new Vector3(0, Mathf.Sign(v.y), 0);
            return new Vector3(0, 0, Mathf.Sign(v.z));
        }

        private Transform FindDeepChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name.ToLower().Contains(name.ToLower()))
                    return child;
                
                Transform result = FindDeepChild(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
