using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 방어 훈련용 추적 카메라
    /// - 활성 아군 선박 우선 추적, 아군 전멸 시 적군 선박 추적
    /// - C키로 수동 전환 (아군 선박들 순환)
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

            GUILayout.BeginArea(new Rect(15, 15, 260, 100), _hudStyle);

            GUILayout.Label($"ALLY:  {allyCount} ships  ({pairCount} pairs)", _hudStyleAlly);
            GUILayout.Label($"ENEMY: {enemyCount} ships", _hudStyleEnemy);

            if (_currentTarget != null)
            {
                string prefix = _followingAllies ? "[Ally]" : "[Enemy]";
                GUILayout.Label($"CAM: {prefix} {_currentTarget.name}  [C=switch]", _hudStyleTarget);
            }
            else
            {
                GUILayout.Label("CAM: No target  [C=switch]", _hudStyleTarget);
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
