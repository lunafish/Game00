using System.Collections.Generic;
using UnityEngine;

namespace CharacterCustomization
{
    /// <summary>
    /// 캐릭터의 본 계층 구조에서 이름에 "socket"이 포함된 Transform을 찾아 매시 파츠를 어태치하고 관리하는 클래스입니다.
    /// </summary>
    public class SocketManager : MonoBehaviour
    {
        [System.Serializable]
        public class SocketAssignment
        {
            public string socketName;
            public GameObject prefab;
            public GameObject currentInstance;
        }

        [Header("Socket Configuration")]
        [SerializeField] private string _socketSearchKeyword = "socket";
        
        [Header("Editor Assignments")]
        [SerializeField] private List<SocketAssignment> _editorAssignments = new List<SocketAssignment>();

        private Dictionary<string, Transform> _socketTransforms = new Dictionary<string, Transform>();
        private Dictionary<string, GameObject> _attachedParts = new Dictionary<string, GameObject>();

        private void Awake()
        {
            RefreshSockets();
            ApplyEditorAssignments();
        }

        /// <summary>
        /// 계층 구조를 다시 탐색하여 소켓 후보군을 갱신하고 에디터 할당 목록을 동기화합니다.
        /// </summary>
        public void RefreshSockets()
        {
            _socketTransforms.Clear();
            FindSocketsRecursive(transform);
            
            // 에디터 할당 목록 동기화
            SyncEditorAssignments();
            
            Debug.Log($"[SocketManager] Found {_socketTransforms.Count} sockets in {gameObject.name}.");
        }

        private void SyncEditorAssignments()
        {
            // 현재 감지된 소켓 이름들을 기반으로 할당 목록 업데이트
            HashSet<string> detectedNames = new HashSet<string>(_socketTransforms.Keys);
            
            // 기존 할당 목록 중 더 이상 존재하지 않는 소켓 제거
            _editorAssignments.RemoveAll(a => !detectedNames.Contains(a.socketName));
            
            // 새로 발견된 소켓 추가
            foreach (var name in detectedNames)
            {
                if (!_editorAssignments.Exists(a => a.socketName == name))
                {
                    _editorAssignments.Add(new SocketAssignment { socketName = name });
                }
            }
        }

        /// <summary>
        /// 에디터에서 설정한 할당 목록을 기반으로 파츠를 부착합니다.
        /// </summary>
        public void ApplyEditorAssignments()
        {
            // 런타임 딕셔너리와 동기화 (기존 인스턴스 정리)
            DetachAllParts();

            foreach (var assignment in _editorAssignments)
            {
                if (assignment.prefab != null)
                {
                    AttachPart(assignment.socketName, assignment.prefab);
                }
            }
        }

        private void FindSocketsRecursive(Transform current)
        {
            // 대소문자 구분 없이 키워드 포함 여부 확인
            if (current.name.ToLower().Contains(_socketSearchKeyword.ToLower()))
            {
                if (!_socketTransforms.ContainsKey(current.name))
                {
                    _socketTransforms.Add(current.name, current);
                }
            }

            foreach (Transform child in current)
            {
                FindSocketsRecursive(child);
            }
        }

        /// <summary>
        /// 지정된 소켓에 프리팹을 생성하여 부착합니다. 이미 파츠가 있다면 제거 후 새로 부착합니다.
        /// </summary>
        public GameObject AttachPart(string socketName, GameObject partPrefab)
        {
            if (partPrefab == null) return null;

            // 런타임 시 소켓을 찾지 못했다면 검색 시도
            if (_socketTransforms.Count == 0 || !_socketTransforms.ContainsKey(socketName))
            {
                RefreshSockets();
            }

            if (!_socketTransforms.TryGetValue(socketName, out Transform socket))
            {
                Debug.LogError($"[SocketManager] Socket '{socketName}' not found.");
                return null;
            }

            // 기존 파츠가 있다면 제거
            DetachPart(socketName);

            // 새 파츠 생성 및 부착
            GameObject newPart = Instantiate(partPrefab, socket);
            newPart.name = $"{socketName}_Part";
            newPart.transform.localPosition = Vector3.zero;
            newPart.transform.localRotation = Quaternion.identity;
            newPart.transform.localScale = Vector3.one;

            _attachedParts[socketName] = newPart;
            
            // 에디터 할당 목록에도 인스턴스 참조 업데이트
            var assignment = _editorAssignments.Find(a => a.socketName == socketName);
            if (assignment != null) assignment.currentInstance = newPart;

            return newPart;
        }

        /// <summary>
        /// 지정된 소켓에서 파츠를 떼어내고 파괴합니다.
        /// </summary>
        public void DetachPart(string socketName)
        {
            // 1. 딕셔너리에 등록된 인스턴스 제거
            if (_attachedParts.TryGetValue(socketName, out GameObject part))
            {
                if (part != null)
                {
                    if (Application.isPlaying)
                        Destroy(part);
                    else
                        DestroyImmediate(part);
                }
                _attachedParts.Remove(socketName);
            }

            // 2. 실제 소켓 Transform 아래에 남아있을 수 있는 _Part 오브젝트들 강제 정리
            if (_socketTransforms.TryGetValue(socketName, out Transform socket))
            {
                for (int i = socket.childCount - 1; i >= 0; i--)
                {
                    var child = socket.GetChild(i);
                    if (child.name.EndsWith("_Part"))
                    {
                        if (Application.isPlaying) Destroy(child.gameObject);
                        else DestroyImmediate(child.gameObject);
                    }
                }
            }

            var assignment = _editorAssignments.Find(a => a.socketName == socketName);
            if (assignment != null) assignment.currentInstance = null;
        }

        /// <summary>
        /// 모든 소켓에서 파츠를 제거합니다.
        /// </summary>
        public void DetachAllParts()
        {
            // 모든 등록된 소켓에 대해 개별 Detach 실행
            List<string> socketNames = new List<string>(_socketTransforms.Keys);
            foreach (var name in socketNames)
            {
                DetachPart(name);
            }

            // 만약 등록되지 않은 소켓이더라도 계층 구조상 하위에 남아있을 수 있는 것들 전체 탐색 및 정리
            ClearOrphanedParts(transform);

            _attachedParts.Clear();
            foreach (var a in _editorAssignments) a.currentInstance = null;
        }

        private void ClearOrphanedParts(Transform current)
        {
            for (int i = current.childCount - 1; i >= 0; i--)
            {
                Transform child = current.GetChild(i);
                if (child.name.EndsWith("_Part"))
                {
                    if (Application.isPlaying) Destroy(child.gameObject);
                    else DestroyImmediate(child.gameObject);
                }
                else
                {
                    ClearOrphanedParts(child);
                }
            }
        }

        /// <summary>
        /// 현재 부착된 파츠 오브젝트를 가져옵니다.
        /// </summary>
        public GameObject GetAttachedPart(string socketName)
        {
            _attachedParts.TryGetValue(socketName, out GameObject part);
            return part;
        }

        /// <summary>
        /// 사용 가능한 소켓 이름 목록을 반환합니다.
        /// </summary>
        public IEnumerable<string> GetAvailableSocketNames()
        {
            return _socketTransforms.Keys;
        }

        public List<SocketAssignment> GetEditorAssignments() => _editorAssignments;
    }
}
