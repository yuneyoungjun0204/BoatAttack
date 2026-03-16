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
            // 방어 훈련 씬이 아니면 무시
            if (Object.FindObjectOfType<DefenseEnvController>() == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            // 이미 붙어있으면 무시
            if (cam.GetComponent<DefenseFollowCamera>() != null) return;

            var followCam = cam.gameObject.AddComponent<DefenseFollowCamera>();
            Debug.LogWarning($"[FollowCam] Main Camera '{cam.name}'에 자동 부착 완료");
        }

        [Header("References")]
        [Tooltip("DefenseEnvController (미설정 시 자동 탐색)")]
        public DefenseEnvController envController;

        [Tooltip("LaunchZoneManager (미설정 시 자동 탐색)")]
        public LaunchZoneManager launchZoneManager;

        [Header("Camera Settings")]
        [Tooltip("카메라 오프셋 (타겟 로컬 좌표)")]
        public Vector3 offset = new Vector3(0f, 5f, -10f);

        [Tooltip("카메라 회전 오프셋 (Euler)")]
        public Vector3 rotationOffset = new Vector3(25f, 0f, 0f);

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

        // C키 전환 직후 자동전환 방지
        private int _switchCooldown = 0;

        // InputAction 기반 C키 (Keyboard.current 폴링보다 안정적)
        private InputAction _switchAction;

        // 탑다운 모드
        private bool _isTopDown = false;
        private Vector3 _topDownPosition;
        private Vector3 _lastMousePos;
        private bool _isDragging = false;

        // 자유 시점 (FreeLook) 모드
        private bool _isFreeLook = false;
        private float _freeLookYaw = 0f;
        private float _freeLookPitch = 20f;
        private float _freeLookMoveSpeed = 50f;
        private float _freeLookSensitivity = 0.15f;

        // Cinemachine 제어
        private Cinemachine.CinemachineBrain _cinemachineBrain;

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
            _cinemachineBrain = GetComponent<Cinemachine.CinemachineBrain>();

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

            HandleInput();
            HandleFreeLookToggle();

            if (_isFreeLook)
            {
                UpdateFreeLookCamera();
                _debugCurrentTarget = "[FreeLook]";
                _debugAllyCount = GetAliveAllies().Count;
                _debugEnemyCount = GetAliveEnemies().Count;
                return;
            }

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
                    if (_cinemachineBrain != null) _cinemachineBrain.enabled = false;
                }
                else
                {
                    if (_cinemachineBrain != null) _cinemachineBrain.enabled = true;
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

        #region FreeLook Camera

        private void HandleFreeLookToggle()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.fKey.wasPressedThisFrame)
            {
                _isFreeLook = !_isFreeLook;
                if (_isFreeLook)
                {
                    // 현재 카메라 방향 유지한 채 전환
                    _freeLookYaw = transform.eulerAngles.y;
                    _freeLookPitch = transform.eulerAngles.x;
                    _isTopDown = false; // 탑다운 해제
                    // Cinemachine이 카메라를 덮어쓰지 않도록 비활성화
                    if (_cinemachineBrain != null) _cinemachineBrain.enabled = false;
                }
                else
                {
                    // Cinemachine 복원
                    if (_cinemachineBrain != null) _cinemachineBrain.enabled = true;
                }
            }
        }

        private void UpdateFreeLookCamera()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null || keyboard == null) return;

            // 우클릭 드래그 → 시점 회전
            if (mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _freeLookYaw += delta.x * _freeLookSensitivity;
                _freeLookPitch -= delta.y * _freeLookSensitivity;
                _freeLookPitch = Mathf.Clamp(_freeLookPitch, -89f, 89f);
            }

            transform.rotation = Quaternion.Euler(_freeLookPitch, _freeLookYaw, 0f);

            // WASD + QE → 이동
            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += transform.forward;
            if (keyboard.sKey.isPressed) move -= transform.forward;
            if (keyboard.dKey.isPressed) move += transform.right;
            if (keyboard.aKey.isPressed) move -= transform.right;
            if (keyboard.eKey.isPressed) move += Vector3.up;
            if (keyboard.qKey.isPressed) move -= Vector3.up;

            // Shift → 가속
            float speed = _freeLookMoveSpeed;
            if (keyboard.leftShiftKey.isPressed) speed *= 3f;

            // 스크롤 → 이동 속도 조절
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                _freeLookMoveSpeed = Mathf.Clamp(_freeLookMoveSpeed + scroll * 5f, 5f, 500f);
            }

            transform.position += move.normalized * speed * Time.unscaledDeltaTime;
        }

        #endregion

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
            if (_isTopDown || _isFreeLook) return;

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
            var allies = GetAliveAllies();
            var enemies = GetAliveEnemies();
            int total = allies.Count + enemies.Count;

            if (total == 0)
            {
                _currentTarget = null;
                return;
            }

            // 통합 리스트: [아군들..., 적군들...]
            var all = new List<GameObject>(allies);
            all.AddRange(enemies);

            // 현재 타겟의 인덱스
            int curIdx = -1;
            if (_currentTarget != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].transform == _currentTarget)
                    {
                        curIdx = i;
                        break;
                    }
                }
            }

            int nextIdx = (curIdx + 1) % all.Count;
            _currentTarget = all[nextIdx].transform;
            _followingAllies = nextIdx < allies.Count;
            _currentIndex = _followingAllies ? nextIdx : nextIdx - allies.Count;
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

            if (_isFreeLook)
            {
                GUILayout.Label($"CAM: [FreeLook]  [F=exit]", _hudStyleTarget);
                GUILayout.Label($"  RDrag=rotate  WASD=move  Scroll=speed", _hudStyleTarget);
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
