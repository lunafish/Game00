using UnityEngine;
using System.Collections.Generic;

namespace ComputeIK
{
    /// <summary>
    /// Compute Shader(FABRIK)를 호출하여 Inverse Kinematics를 해결하고,
    /// 뼈의 위치와 회전을 업데이트하는 메인 컨트롤러입니다.
    /// </summary>
    public class ComputeIK : MonoBehaviour
    {
        public ComputeShader computeShader;
        
        [System.Serializable]
        public class IKChain
        {
            public string name;
            public Transform target;          // 관절이 도달해야 할 목표 지점
            public Transform poleTarget;      // 무릎/팔꿈치가 향해야 할 방향 (Hint)
            public Transform[] bones;         // 관절을 이루는 Transform 배열
            public bool usePole = true;       // Pole 타겟 사용 여부
            public bool updateRotation = true; // IK 해결 후 뼈의 회전값을 업데이트할지 여부
            public JointLimit[] jointLimits;  // 각 관절의 각도 제한

            [HideInInspector] public ComputeBuffer boneBuffer;
            [HideInInspector] public ComputeBuffer lengthBuffer;
            [HideInInspector] public ComputeBuffer limitBuffer;
            [HideInInspector] public Bone[] boneData;
            [HideInInspector] public float[] lengthData;
            [HideInInspector] public InitialBoneState[] initialStates;

            public void Release()
            {
                if (boneBuffer != null) boneBuffer.Release();
                if (lengthBuffer != null) lengthBuffer.Release();
                if (limitBuffer != null) limitBuffer.Release();
            }
        }

        // Compute Shader와 통신하기 위한 데이터 구조체
        public struct Bone {
            public Vector3 position;
        }

        /// <summary>
        /// 초기 뼈 상태를 저장하는 구조체입니다.
        /// Root(몸체) 기준의 로컬 데이터를 저장하여 180도 회전 시의 메시 꼬임을 방지합니다.
        /// </summary>
        public struct InitialBoneState {
            public Quaternion localRotation;  // 몸체 기준 로컬 회전
            public Vector3 localDirection;    // 몸체 기준 로컬 뼈 방향
            public Vector3 localPoleDir;      // 뼈 기준 로컬 Pole 방향 (Twist 계산용)
        }

        [System.Serializable]
        public struct JointLimit {
            public float minAngle;
            public float maxAngle;
            public int enabled;
        }

        public List<IKChain> chains = new List<IKChain>();
        
        void Start()
        {
            foreach (var chain in chains)
            {
                InitializeChain(chain);
            }
        }

        /// <summary>
        /// IK 체인을 분석하고 초기 상태(Reference State)를 캡처합니다.
        /// </summary>
        void InitializeChain(IKChain chain)
        {
            if (chain.bones == null || chain.bones.Length < 2) 
            {
                Debug.LogWarning($"Chain {chain.name} must have at least 2 bones.");
                return;
            }

            if (chain.jointLimits == null || chain.jointLimits.Length != chain.bones.Length)
            {
                System.Array.Resize(ref chain.jointLimits, chain.bones.Length);
            }
            
            chain.boneData = new Bone[chain.bones.Length];
            chain.lengthData = new float[chain.bones.Length - 1];
            chain.initialStates = new InitialBoneState[chain.bones.Length - 1];
            
            for (int i = 0; i < chain.bones.Length; i++) {
                chain.boneData[i].position = chain.bones[i].position;
                if (i < chain.bones.Length - 1) {
                    chain.lengthData[i] = Vector3.Distance(chain.bones[i].position, chain.bones[i+1].position);
                    
                    // [Stability Fix] 초기 회전과 방향을 Root(몸체) 기준으로 로컬화하여 저장합니다.
                    // 이를 통해 로봇이 뒤를 돌아도(180도 회전) 기준점이 함께 돌아가므로 회전 계산이 꼬이지 않습니다.
                    chain.initialStates[i].localRotation = Quaternion.Inverse(transform.rotation) * chain.bones[i].rotation;
                    
                    Vector3 worldDir = (chain.bones[i+1].position - chain.bones[i].position).normalized;
                    chain.initialStates[i].localDirection = transform.InverseTransformDirection(worldDir);
                    
                    if (chain.poleTarget != null)
                    {
                        // 뼈 기준으로 Pole 타겟이 어느 로컬 방향에 있었는지 캡처 (Twist 보정용)
                        Vector3 poleDirWorld = (chain.poleTarget.position - chain.bones[i].position).normalized;
                        Vector3 poleDirInBoneSpace = chain.bones[i].InverseTransformDirection(poleDirWorld);
                        Vector3 boneAxisInBoneSpace = chain.bones[i].InverseTransformDirection(worldDir);
                        
                        chain.initialStates[i].localPoleDir = Vector3.ProjectOnPlane(poleDirInBoneSpace, boneAxisInBoneSpace).normalized;
                    }
                }
            }
            
            // GPU 메모리 할당
            chain.boneBuffer = new ComputeBuffer(chain.bones.Length, 12);
            chain.lengthBuffer = new ComputeBuffer(chain.bones.Length - 1, 4);
            chain.limitBuffer = new ComputeBuffer(chain.bones.Length, 12);
            
            chain.lengthBuffer.SetData(chain.lengthData);
            chain.limitBuffer.SetData(chain.jointLimits);
        }
        
        void Update()
        {
            if (computeShader == null) return;

            foreach (var chain in chains)
            {
                UpdateChain(chain);
            }
        }

        /// <summary>
        /// GPU에 데이터를 전달하여 관절 위치를 계산(FABRIK)하고, 결과를 받아 회전을 업데이트합니다.
        /// </summary>
        void UpdateChain(IKChain chain)
        {
            if (chain.boneBuffer == null || chain.target == null) return;
            
            for(int i=0; i<chain.bones.Length; i++) chain.boneData[i].position = chain.bones[i].position;
            
            chain.boneBuffer.SetData(chain.boneData);
            chain.limitBuffer.SetData(chain.jointLimits);
            
            // GPU 파라미터 설정
            int kernel = computeShader.FindKernel("CSMain");
            computeShader.SetBuffer(kernel, "Bones", chain.boneBuffer);
            computeShader.SetBuffer(kernel, "BoneLengths", chain.lengthBuffer);
            computeShader.SetBuffer(kernel, "JointLimits", chain.limitBuffer);
            computeShader.SetVector("TargetPosition", chain.target.position);
            computeShader.SetVector("PolePosition", chain.poleTarget != null ? chain.poleTarget.position : Vector3.zero);
            computeShader.SetInt("BoneCount", chain.bones.Length);
            computeShader.SetInt("UsePole", (chain.usePole && chain.poleTarget != null) ? 1 : 0);

            // Compute Shader 실행
            computeShader.Dispatch(kernel, 1, 1, 1);
            
            // 결과 데이터 가져오기 (위치값)
            chain.boneBuffer.GetData(chain.boneData);
            
            for (int i = 0; i < chain.bones.Length; i++) {
                chain.bones[i].position = chain.boneData[i].position;
                
                // 회전 업데이트 로직
                if (chain.updateRotation && i < chain.bones.Length - 1) {
                    Vector3 currentDir = (chain.bones[i+1].position - chain.bones[i].position).normalized;
                    if(currentDir != Vector3.zero)
                    {
                        // [Root Sync] 저장해둔 로컬 기준점을 현재 몸체 회전에 맞춰 월드로 변환
                        Vector3 refDir = transform.TransformDirection(chain.initialStates[i].localDirection);
                        Quaternion refRot = transform.rotation * chain.initialStates[i].localRotation;

                        // 1. Swing: 뼈가 목표 방향(currentDir)으로 향하게 함
                        Quaternion swing = Quaternion.FromToRotation(refDir, currentDir);
                        Quaternion targetRotation = swing * refRot;

                        // 2. Twist: 뼈가 정해진 축(Pole)을 바라보게 함
                        if (chain.poleTarget != null)
                        {
                            Vector3 boneAxis = currentDir;
                            Vector3 poleDir = (chain.poleTarget.position - chain.bones[i].position).normalized;
                            Vector3 projectedPoleDir = Vector3.ProjectOnPlane(poleDir, boneAxis).normalized;
                            
                            if (chain.initialStates[i].localPoleDir != Vector3.zero)
                            {
                                // 초기 Pole 방향을 현재 회전값 기준으로 투영하여 오차(Twist) 계산
                                Vector3 currentLocalPoleDir = targetRotation * chain.initialStates[i].localPoleDir;
                                Vector3 projectedLocalPoleDir = Vector3.ProjectOnPlane(currentLocalPoleDir, boneAxis).normalized;

                                if (projectedPoleDir != Vector3.zero && projectedLocalPoleDir != Vector3.zero)
                                {
                                    Quaternion twist = Quaternion.FromToRotation(projectedLocalPoleDir, projectedPoleDir);
                                    targetRotation = twist * targetRotation;
                                }
                            }
                        }
                        chain.bones[i].rotation = targetRotation;
                    }
                }
            }
        }
        
        void OnDestroy()
        {
            foreach (var chain in chains) chain.Release();
        }

        void OnDrawGizmos()
        {
            if (chains == null) return;
            foreach (var chain in chains)
            {
                if (chain.bones == null || chain.bones.Length < 2) continue;
                Gizmos.color = Color.green;
                for (int i = 0; i < chain.bones.Length - 1; i++) {
                    if (chain.bones[i] != null && chain.bones[i + 1] != null)
                        Gizmos.DrawLine(chain.bones[i].position, chain.bones[i + 1].position);
                }
                if (chain.poleTarget != null) { Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(chain.poleTarget.position, 0.05f); }
                if (chain.target != null) { Gizmos.color = Color.red; Gizmos.DrawWireSphere(chain.target.position, 0.05f); }
            }
        }
    }
}
