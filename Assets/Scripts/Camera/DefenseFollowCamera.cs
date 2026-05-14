using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 방어 훈련용 추적 카메라
    /// - 활성 아군 선박 우선 추적, 아군 전멸 시 적군 선박 추적
    /// - C키로 수동 전환 (아군 선박들 순환)
    /// - 우클릭으로 전지적 시점(탑다운) 토글, 좌클릭 드래그로 이동
    /// - 선박 무력화 시 자동으로 다음 활성 선박으로 전환
    /// - OnGUI로 아군/적군 생존 수 HUD 표시
    /// </summary>
    public class DefenseFollowCamera : MonoBehaviour
    {
        /// <summary>
        /// 씬 로드 후 자동으로 Main Camera에 부착 (DefenseEnvController가 있는 씬에서만)
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoAttachToMainCamera()
        {
            if (Object.FindObjectOfType<DefenseEnvController>() == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            // 씬 뷰 에디터 카메라 제외 (___LINK__SCENE__VIEW__CAMERA___ 등)
            string camName = cam.gameObject.name;
            if (camName.Contains("LINK__SCENE") || camName.Contains("SceneCamera") || camName.StartsWith("___"))
            {
                Debug.LogWarning($"[FollowCam] 씬뷰 카메라 감지 — 자동 부착 건너뜀: {camName}");
                return;
            }

            if (cam.GetComponent<DefenseFollowCamera>() != null) return;

            cam.gameObject.AddComponent<DefenseFollowCamera>();
            Debug.LogWarning($"[FollowCam] Main Camera '{camName}'에 자동 부착 완료");
        }

        [Header("References")]
        [Tooltip("DefenseEnvController (미설정 시 자동 탐색)")]
        public DefenseEnvController envController;

        [Tooltip("LaunchZoneManager (미설정 시 자동 탐색)")]
        public LaunchZoneManager launchZoneManager;

        [Header("Extra Cameras")]
        [Tooltip("C키 순환에 포함할 추가 카메라 오브젝트들 (Inspector 할당 or 자동 탐색)")]
        public GameObject[] extraCameraObjects;

        [Header("Camera Settings")]
        [Tooltip("카메라 오프셋 (타겟 로컬 좌표)")]
        public Vector3 offset = new Vector3(0f, 250f, -400f);

        [Tooltip("카메라 회전 오프셋 (Euler)")]
        public Vector3 rotationOffset = new Vector3(5f, 0f, 0f);

        [Tooltip("위치 추적 속도")]
        public float followSpeed = 8f;

        [Tooltip("회전 추적 속도")]
        public float rotationSpeed = 4f;

        [Header("Top-Down View")]
        [Tooltip("탑다운 카메라 높이")]
        public float topDownHeight = 300f;

        [Tooltip("탑다운 드래그 이동 속도")]
        public float topDownDragSpeed = 1.5f;

        [Tooltip("탑다운 줌 속도 (스크롤)")]
        public float topDownZoomSpeed = 30f;

        [Tooltip("탑다운 최소 높이")]
        public float topDownMinHeight = 100f;

        [Tooltip("탑다운 최대 높이")]
        public float topDownMaxHeight = 800f;

        [Header("HUD")]
        [Tooltip("게임 화면에 아군/적군 생존 수 표시")]
        public bool showShipCountHUD = true;

        [Header("Debug")]
        [SerializeField] private string _debugCurrentTarget = "None";
        [SerializeField] private bool _debugFollowingAllies = true;
        [SerializeField] private int _debugAllyCount = 0;
        [SerializeField] private int _debugEnemyCount = 0;

        private Transform _currentTarget;
        private int _currentIndex = 0;
        private bool _followingAllies = true;
        private bool _isExtraCamera = false;  // 현재 추가 카메라 시점인지

        // 추가 카메라 전환 시 Main Camera를 끄고 해당 카메라를 켬
        private UnityEngine.Camera _activatedExtraCam;
        private UnityEngine.Camera _thisCam;  // 이 스크립트가 붙은 오브젝트의 카메라

        // 드론 조종 상태
        [Header("Drone Control")]
        [Tooltip("드론 이동 속도 (m/s)")]
        public float droneMoveSpeed = 30f;
        [Tooltip("드론 회전 속도 (도/초)")]
        public float droneRotateSpeed = 90f;
        [Tooltip("드론 스크롤 속도 배율")]
        public float droneScrollStep = 10f;
        private float _droneYaw;
        private float _dronePitch;

        // C키 전환 직후 자동전환 방지
        private int _switchCooldown = 0;

        // InputAction 기반 C키 (Keyboard.current 폴링보다 안정적)
        private InputAction _switchAction;

        // 탑다운 모드
        private bool _isTopDown = false;
        private Vector3 _topDownPosition;
        private Vector3 _lastMousePos;
        private bool _isDragging = false;

        // FreeFlyCamera 참조 (F키는 FreeFlyCamera가 전담)
        private FreeFlyCamera _freeFlyCamera;

        // Cinemachine 제어 (씬 내 모든 Brain)
        private Cinemachine.CinemachineBrain[] _allBrains;

        // OnGUI 스타일 캐시
        private GUIStyle _hudStyle;
        private GUIStyle _hudStyleAlly;
        private GUIStyle _hudStyleEnemy;
        private GUIStyle _hudStyleTarget;

        private void Start()
        {
            if (envController == null)
                envController = FindObjectOfType<DefenseEnvController>();
            if (launchZoneManager == null)
                launchZoneManager = FindObjectOfType<LaunchZoneManager>();
            _allBrains = FindObjectsOfType<Cinemachine.CinemachineBrain>();
            _thisCam = GetComponent<UnityEngine.Camera>();  // 이 오브젝트의 카메라 고정 참조

            // extraCameraObjects가 비어있으면 "Camera Drone" 이름으로 자동 탐색
            if (extraCameraObjects == null || extraCameraObjects.Length == 0)
            {
                var droneObj = GameObject.Find("Camera Drone");
                if (droneObj != null)
                {
                    extraCameraObjects = new GameObject[] { droneObj };
                    Debug.Log("[FollowCam] Camera Drone 자동 등록 완료");
                }
            }

            _freeFlyCamera = GetComponent<FreeFlyCamera>();
            if (_freeFlyCamera == null)
                _freeFlyCamera = FindObjectOfType<FreeFlyCamera>();

            Debug.LogWarning($"[FollowCam] START: env={envController != null}, lzm={launchZoneManager != null}");
        }

        private void OnEnable()
        {
            _switchAction = new InputAction("SwitchCamera", InputActionType.Button, "<Keyboard>/c");
            _switchAction.Enable();
        }

        private void OnDisable()
        {
            if (_switchAction != null)
            {
                _switchAction.Disable();
                _switchAction.Dispose();
                _switchAction = null;
            }
        }

        private void LateUpdate()
        {
            if (_switchCooldown > 0)
                _switchCooldown--;

            // FreeFlyCamera가 활성이면 이 카메라는 아무것도 안 함
            if (_freeFlyCamera != null && _freeFlyCamera.IsActive)
                return;

            HandleInput();

            HandleTopDownInput();

            if (_isTopDown)
            {
                UpdateTopDownCamera();

                // Inspector 디버그
                _debugCurrentTarget = "[Top-Down]";
                _debugAllyCount = GetAliveAllies().Count;
                _debugEnemyCount = GetAliveEnemies().Count;
                return;
            }

            // 추가 카메라 시점이면 드론 조종 처리
            if (_isExtraCamera)
            {
                _debugCurrentTarget = _currentTarget != null ? $"[Cam] {_currentTarget.name}" : "None";
                _debugAllyCount = GetAliveAllies().Count;
                _debugEnemyCount = GetAliveEnemies().Count;
                HandleDroneControl();
                return;
            }

            // C키 직후 3프레임은 자동 전환 건너뜀
            if (_switchCooldown <= 0 && !IsTargetAlive())
            {
                SwitchToNextAlive();
            }

            FollowTarget();

            // Inspector 디버그
            _debugCurrentTarget = _currentTarget != null ? _currentTarget.name : "None";
            _debugFollowingAllies = _followingAllies;
            _debugAllyCount = GetAliveAllies().Count;
            _debugEnemyCount = GetAliveEnemies().Count;
        }

        #region Top-Down View

        private void HandleTopDownInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // 우클릭 → Follow ↔ Top-Down 토글
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _isTopDown = !_isTopDown;
                if (_isTopDown)
                {
                    _topDownPosition = transform.position;
                    _topDownPosition.y = 0f;
                    _isDragging = false;
                    SetAllBrains(false);
                }
                else
                {
                    SetAllBrains(true);
                }
            }

            if (!_isTopDown) return;

            // 좌클릭 드래그 → 카메라 XZ 이동
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _lastMousePos = mouse.position.ReadValue();
            }
            if (mouse.leftButton.wasReleasedThisFrame)
            {
                _isDragging = false;
            }

            if (_isDragging)
            {
                Vector3 currentMousePos = mouse.position.ReadValue();
                Vector3 delta = currentMousePos - _lastMousePos;
                float scale = topDownDragSpeed * (topDownHeight / 300f);
                _topDownPosition -= new Vector3(delta.x, 0f, delta.y) * scale * Time.unscaledDeltaTime;
                _lastMousePos = currentMousePos;
            }

            // 스크롤 → 줌 인/아웃
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                topDownHeight -= scroll * topDownZoomSpeed;
                topDownHeight = Mathf.Clamp(topDownHeight, topDownMinHeight, topDownMaxHeight);
            }
        }

        private void UpdateTopDownCamera()
        {
            Vector3 desiredPos = _topDownPosition + Vector3.up * topDownHeight;
            transform.position = Vector3.Lerp(transform.position, desiredPos, 10f * Time.unscaledDeltaTime);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        #endregion

        // FreeLook 기능은 FreeFlyCamera.cs로 이전됨

        private bool IsTargetAlive()
        {
            if (_currentTarget == null) return false;

            if (_followingAllies && launchZoneManager != null)
            {
                int capacity = launchZoneManager.GetPoolCapacity();
                for (int i = 0; i < capacity; i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair == null) continue;

                    bool isThisPair = (pair.agent1 != null && pair.agent1.transform == _currentTarget)
                                   || (pair.agent2 != null && pair.agent2.transform == _currentTarget);
                    if (isThisPair)
                        return pair.isActive;
                }
                return false;
            }

            if (!_followingAllies && envController != null)
                return !envController.IsEnemyNeutralized(_currentTarget.gameObject);

            return _currentTarget.gameObject.activeInHierarchy;
        }

        private void HandleInput()
        {
            // 탑다운/자유 시점 모드에서는 C키 무시
            if (_isTopDown) return;
            if (_freeFlyCamera != null && _freeFlyCamera.IsActive) return;

            // InputAction 기반 (가장 안정적)
            bool pressed = _switchAction != null && _switchAction.WasPressedThisFrame();

            // Fallback: Keyboard.current 직접 폴링
            if (!pressed)
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                    pressed = keyboard.cKey.wasPressedThisFrame;
            }

            if (pressed)
            {
                var allies = GetAliveAllies();
                var enemies = GetAliveEnemies();
                Debug.LogWarning($"[FollowCam] C키! allies={allies.Count}, enemies={enemies.Count}, " +
                    $"curTarget={(_currentTarget != null ? _currentTarget.name : "None")}");

                SwitchToNext();
                _switchCooldown = 3;

                Debug.LogWarning($"[FollowCam] → newTarget={(_currentTarget != null ? _currentTarget.name : "None")}, followAllies={_followingAllies}");
            }
        }

        private void SwitchToNext()
        {
            var allies  = GetAliveAllies();
            var enemies = GetAliveEnemies();
            var extras  = GetValidExtraCameras();

            // 통합 리스트: [아군들..., 적군들..., 추가카메라들...]
            var all = new List<GameObject>(allies);
            all.AddRange(enemies);

            int total = all.Count + extras.Count;
            if (total == 0) { _currentTarget = null; return; }

            // 현재 타겟 인덱스 탐색 — DeactivateExtraCamera 전에 먼저 찾아야 함
            int curIdx = -1;
            if (_currentTarget != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].transform == _currentTarget) { curIdx = i; break; }
                }
                if (curIdx < 0 && _isExtraCamera)
                {
                    for (int i = 0; i < extras.Count; i++)
                    {
                        if (extras[i].transform == _currentTarget)
                        { curIdx = all.Count + i; break; }
                    }
                }
            }

            // 현재 추가 카메라 시점이면 해제 (인덱스 탐색 이후)
            if (_isExtraCamera)
                DeactivateExtraCamera();

            int nextIdx = (curIdx + 1) % total;

            if (nextIdx < all.Count)
            {
                // 선박 시점
                _isExtraCamera   = false;
                _currentTarget   = all[nextIdx].transform;
                _followingAllies = nextIdx < allies.Count;
                _currentIndex    = _followingAllies ? nextIdx : nextIdx - allies.Count;
            }
            else
            {
                // 추가 카메라 시점
                var extraObj     = extras[nextIdx - all.Count];
                _isExtraCamera   = true;
                _currentTarget   = extraObj.transform;
                _followingAllies = false;
                ActivateExtraCamera(extraObj);
            }
        }

        private List<GameObject> GetValidExtraCameras()
        {
            var result = new List<GameObject>();
            if (extraCameraObjects == null) return result;
            foreach (var obj in extraCameraObjects)
                if (obj != null) result.Add(obj);
            return result;
        }

        private void ActivateExtraCamera(GameObject camObj)
        {
            var cam = camObj.GetComponent<UnityEngine.Camera>();
            if (cam == null)
            {
                cam = camObj.AddComponent<UnityEngine.Camera>();
                if (_thisCam != null)
                {
                    cam.fieldOfView     = _thisCam.fieldOfView;
                    cam.nearClipPlane   = _thisCam.nearClipPlane;
                    cam.farClipPlane    = _thisCam.farClipPlane;
                    cam.clearFlags      = _thisCam.clearFlags;
                    cam.backgroundColor = _thisCam.backgroundColor;
                    cam.cullingMask     = _thisCam.cullingMask;
                }
            }

            // 이 스크립트의 카메라 OFF, 추가 카메라 ON
            if (_thisCam != null) _thisCam.enabled = false;
            cam.enabled = true;
            _activatedExtraCam = cam;

            // 드론 현재 회전으로 yaw/pitch 초기화
            _droneYaw   = camObj.transform.eulerAngles.y;
            _dronePitch = camObj.transform.eulerAngles.x;
            if (_dronePitch > 180f) _dronePitch -= 360f;

            Debug.LogWarning($"[FollowCam] ★ 드론 카메라 ON: {camObj.name}  WASD로 조종하세요");
        }

        private void DeactivateExtraCamera()
        {
            if (_activatedExtraCam != null)
            {
                _activatedExtraCam.enabled = false;
                _activatedExtraCam = null;
            }

            // 이 스크립트의 카메라 복원
            if (_thisCam != null) _thisCam.enabled = true;
            _isExtraCamera = false;
        }

        private void HandleDroneControl()
        {
            if (_currentTarget == null) return;

            var kb = Keyboard.current;
            var mouse = Mouse.current;

            // 이동
            Vector3 move = Vector3.zero;
            Quaternion rot = Quaternion.Euler(_dronePitch, _droneYaw, 0f);
            if (kb != null)
            {
                if (kb.wKey.isPressed) move += rot * Vector3.forward;
                if (kb.sKey.isPressed) move -= rot * Vector3.forward;
                if (kb.aKey.isPressed) move -= rot * Vector3.right;
                if (kb.dKey.isPressed) move += rot * Vector3.right;
                if (kb.spaceKey.isPressed)     move += Vector3.up;
                if (kb.leftShiftKey.isPressed) move -= Vector3.up;
            }
            if (move.sqrMagnitude > 0.01f)
                _currentTarget.position += move.normalized * droneMoveSpeed * Time.unscaledDeltaTime;

            // 회전: 우클릭 드래그 or QE/RZ
            float yawDelta = 0f, pitchDelta = 0f;
            if (kb != null)
            {
                if (kb.qKey.isPressed) yawDelta   -= droneRotateSpeed * Time.unscaledDeltaTime;
                if (kb.eKey.isPressed) yawDelta   += droneRotateSpeed * Time.unscaledDeltaTime;
                if (kb.rKey.isPressed) pitchDelta -= droneRotateSpeed * Time.unscaledDeltaTime;
                if (kb.zKey.isPressed) pitchDelta += droneRotateSpeed * Time.unscaledDeltaTime;
            }
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                yawDelta   += delta.x *  0.3f;
                pitchDelta += delta.y * -0.3f;
            }
            _droneYaw   += yawDelta;
            _dronePitch  = Mathf.Clamp(_dronePitch + pitchDelta, -85f, 85f);
            _currentTarget.rotation = Quaternion.Euler(_dronePitch, _droneYaw, 0f);

            // 스크롤: 이동 속도 조절
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    droneMoveSpeed = Mathf.Clamp(droneMoveSpeed + (scroll > 0 ? droneScrollStep : -droneScrollStep), 5f, 300f);
            }
        }

        private void SwitchToNextAlive()
        {
            var allies = GetAliveAllies();
            if (allies.Count > 0)
            {
                _followingAllies = true;
                _currentIndex = Mathf.Clamp(_currentIndex, 0, allies.Count - 1);
                _currentTarget = allies[_currentIndex].transform;
                return;
            }

            var enemies = GetAliveEnemies();
            if (enemies.Count > 0)
            {
                _followingAllies = false;
                _currentIndex = Mathf.Clamp(_currentIndex, 0, enemies.Count - 1);
                _currentTarget = enemies[_currentIndex].transform;
                return;
            }

            _currentTarget = null;
        }

        private List<GameObject> GetAliveAllies()
        {
            var result = new List<GameObject>();

            if (launchZoneManager != null)
            {
                int capacity = launchZoneManager.GetPoolCapacity();
                for (int i = 0; i < capacity; i++)
                {
                    var pair = launchZoneManager.GetPair(i);
                    if (pair == null || !pair.isActive) continue;

                    if (pair.agent1 != null) result.Add(pair.agent1.gameObject);
                    if (pair.agent2 != null) result.Add(pair.agent2.gameObject);
                }
            }
            else if (envController != null)
            {
                if (envController.defenseAgent1 != null && envController.defenseAgent1.gameObject.activeInHierarchy)
                    result.Add(envController.defenseAgent1.gameObject);
                if (envController.defenseAgent2 != null && envController.defenseAgent2.gameObject.activeInHierarchy)
                    result.Add(envController.defenseAgent2.gameObject);
            }

            return result;
        }

        private List<GameObject> GetAliveEnemies()
        {
            var result = new List<GameObject>();

            if (envController != null && envController.enemyShips != null)
            {
                foreach (var enemy in envController.enemyShips)
                {
                    if (enemy != null && !envController.IsEnemyNeutralized(enemy))
                        result.Add(enemy);
                }
            }

            return result;
        }

        private void FollowTarget()
        {
            if (_currentTarget == null) return;

            Vector3 desiredPos = _currentTarget.position
                + _currentTarget.right * offset.x
                + Vector3.up * offset.y
                + _currentTarget.forward * offset.z;

            transform.position = Vector3.Lerp(transform.position, desiredPos, followSpeed * Time.deltaTime);

            Quaternion lookRot = Quaternion.LookRotation(_currentTarget.position - transform.position);
            Quaternion desiredRot = lookRot * Quaternion.Euler(rotationOffset);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotationSpeed * Time.deltaTime);
        }

        public void OnShipNeutralized(GameObject ship)
        {
            if (_currentTarget != null && _currentTarget.gameObject == ship)
            {
                SwitchToNextAlive();
            }
        }

        public void OnEpisodeReset()
        {
            if (_isExtraCamera)
                DeactivateExtraCamera();
            _currentIndex = 0;
            _followingAllies = true;
            _currentTarget = null;
            _switchCooldown = 0;
            // 탑다운 모드 유지 (에피소드 리셋 시에도)
        }

        public DefenseAgent CurrentDefenseAgent
        {
            get
            {
                if (_currentTarget == null || !_followingAllies) return null;
                return _currentTarget.GetComponent<DefenseAgent>();
            }
        }

        private void SetAllBrains(bool enabled)
        {
            if (_allBrains == null) return;
            foreach (var brain in _allBrains)
                if (brain != null) brain.enabled = enabled;
        }

        #region OnGUI HUD

        private void InitGUIStyles()
        {
            if (_hudStyle != null) return;

            _hudStyle = new GUIStyle(GUI.skin.box);
            _hudStyle.normal.background = MakeTex(2, 2, new Color(0.05f, 0.08f, 0.15f, 0.85f));
            _hudStyle.padding = new RectOffset(10, 10, 8, 8);

            _hudStyleAlly = new GUIStyle(GUI.skin.label);
            _hudStyleAlly.fontSize = 16;
            _hudStyleAlly.fontStyle = FontStyle.Bold;
            _hudStyleAlly.normal.textColor = new Color(0.2f, 0.85f, 1f);

            _hudStyleEnemy = new GUIStyle(GUI.skin.label);
            _hudStyleEnemy.fontSize = 16;
            _hudStyleEnemy.fontStyle = FontStyle.Bold;
            _hudStyleEnemy.normal.textColor = new Color(1f, 0.3f, 0.25f);

            _hudStyleTarget = new GUIStyle(GUI.skin.label);
            _hudStyleTarget.fontSize = 14;
            _hudStyleTarget.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }

        private void OnGUI()
        {
            if (!showShipCountHUD) return;

            InitGUIStyles();

            int allyCount = _debugAllyCount;
            int enemyCount = _debugEnemyCount;
            int pairCount = 0;
            if (launchZoneManager != null)
                pairCount = launchZoneManager.GetActivePairCount();

            int capturedCount = 0;
            int breachedCount = 0;
            if (envController != null)
            {
                capturedCount = envController.GetCapturedEnemyCount();
                breachedCount = envController.GetBreachedEnemyCount();
            }

            GUILayout.BeginArea(new Rect(Screen.width - 275, 15, 260, 155), _hudStyle);

            GUILayout.Label($"ALLY:  {allyCount} ships  ({pairCount} pairs)", _hudStyleAlly);
            GUILayout.Label($"ENEMY: {enemyCount} ships", _hudStyleEnemy);
            GUILayout.Label($"  Captured: {capturedCount}  |  Breached: {breachedCount}", _hudStyleTarget);

            if (_isExtraCamera && _currentTarget != null)
            {
                GUILayout.Label($"CAM: [Drone] {_currentTarget.name}  [C=next]  speed:{droneMoveSpeed:F0}", _hudStyleTarget);
                GUILayout.Label($"  WASD=이동  Space/Shift=상하  QE/RZ=회전  RDrag=마우스회전  Scroll=속도", _hudStyleTarget);
            }
            else if (_freeFlyCamera != null && _freeFlyCamera.IsActive)
            {
                GUILayout.Label($"CAM: [FreeFly]  [F=exit]", _hudStyleTarget);
                GUILayout.Label($"  WASD=move  RDrag=rotate  Scroll=speed", _hudStyleTarget);
            }
            else if (_isTopDown)
            {
                GUILayout.Label($"CAM: [Top-Down]  [RClick=follow]  [F=free]", _hudStyleTarget);
                GUILayout.Label($"  Drag=move  Scroll=zoom", _hudStyleTarget);
            }
            else if (_currentTarget != null)
            {
                string prefix = _followingAllies ? "[Ally]" : "[Enemy]";
                GUILayout.Label($"CAM: {prefix} {_currentTarget.name}  [C=switch]", _hudStyleTarget);
                GUILayout.Label($"  [RClick=top-down]  [F=free]", _hudStyleTarget);
            }
            else
            {
                GUILayout.Label("CAM: No target  [C=switch]", _hudStyleTarget);
                GUILayout.Label($"  [RClick=top-down]  [F=free]", _hudStyleTarget);
            }

            GUILayout.EndArea();
        }

        private static Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        #endregion
    }
}
