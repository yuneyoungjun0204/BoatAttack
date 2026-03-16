using UnityEngine;
using UnityEngine.InputSystem;

namespace BoatAttack
{
    /// <summary>
    /// 자유 비행 CCTV 카메라
    /// - C키로 DefenseFollowCamera 대상 순환 중 빈 Cube 오브젝트를 대상으로 선택하면 활성화
    /// - WASD: 수평 이동, Space/Shift: 상승/하강
    /// - R/Z: Pitch(상하 회전), Q/C: Yaw(좌우 회전)
    /// - 마우스 스크롤: 이동 속도 조절
    /// - ESC 또는 C키: 다시 DefenseFollowCamera 대상 순환으로 복귀
    ///
    /// === 사용 가이드 ===
    /// 1. Unity에서 빈 GameObject 생성 (Hierarchy → Create Empty)
    /// 2. 이름을 "FreeFlyTarget"으로 변경
    /// 3. Tag를 "FreeFlyCamera"로 설정 (없으면 Tag Manager에서 추가)
    /// 4. 이 스크립트를 Main Camera에 부착 (자동 부착됨)
    /// 5. 플레이 후 C키를 눌러 대상 순환 → "FreeFlyTarget"이 선택되면 자유 비행 모드 진입
    ///    또는 F키를 눌러 즉시 자유 비행 모드 토글
    /// </summary>
    public class FreeFlyCamera : MonoBehaviour
    {
        [Header("이동 설정")]
        [Tooltip("기본 이동 속도 (m/s)")]
        public float moveSpeed = 50f;

        [Tooltip("최소 이동 속도")]
        public float minSpeed = 10f;

        [Tooltip("최대 이동 속도")]
        public float maxSpeed = 300f;

        [Tooltip("스크롤 속도 변경 계수")]
        public float scrollSpeedStep = 10f;

        [Header("회전 설정")]
        [Tooltip("키보드 회전 속도 (도/초)")]
        public float rotateSpeed = 90f;

        [Tooltip("Pitch 제한 (도)")]
        public float pitchLimit = 85f;

        [Header("상태")]
        [SerializeField] private bool _isActive = false;

        private float _yaw;
        private float _pitch;
        private Cinemachine.CinemachineBrain _cinemachineBrain;
        private DefenseFollowCamera _followCamera;

        /// <summary>자유 비행 모드 활성 여부</summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// 씬 로드 후 자동으로 Main Camera에 부착
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoAttach()
        {
            if (Object.FindObjectOfType<DefenseEnvController>() == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<FreeFlyCamera>() != null) return;
            cam.gameObject.AddComponent<FreeFlyCamera>();
        }

        private void Start()
        {
            _cinemachineBrain = GetComponent<Cinemachine.CinemachineBrain>();
            _followCamera = GetComponent<DefenseFollowCamera>();
            _yaw = transform.eulerAngles.y;
            _pitch = transform.eulerAngles.x;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            // F키: 자유 비행 모드 토글
            if (kb.fKey.wasPressedThisFrame)
            {
                if (_isActive)
                    Deactivate();
                else
                    Activate();
                return;
            }

            if (!_isActive) return;

            // ESC: 자유 비행 종료
            if (kb.escapeKey.wasPressedThisFrame)
            {
                Deactivate();
                return;
            }

            HandleMovement(kb);
            HandleRotation(kb);
            HandleSpeed();
        }

        /// <summary>자유 비행 모드 활성화</summary>
        public void Activate()
        {
            _isActive = true;
            _yaw = transform.eulerAngles.y;
            _pitch = transform.eulerAngles.x;
            if (_pitch > 180f) _pitch -= 360f;

            // Cinemachine 비활성화
            if (_cinemachineBrain != null)
                _cinemachineBrain.enabled = false;

            // FollowCamera 비활성화
            if (_followCamera != null)
                _followCamera.enabled = false;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            Debug.Log($"[FreeFlyCamera] 자유 비행 모드 ON (속도: {moveSpeed}m/s)");
        }

        /// <summary>자유 비행 모드 비활성화</summary>
        public void Deactivate()
        {
            _isActive = false;

            // Cinemachine 복원
            if (_cinemachineBrain != null)
                _cinemachineBrain.enabled = true;

            // FollowCamera 복원
            if (_followCamera != null)
                _followCamera.enabled = true;

            Debug.Log("[FreeFlyCamera] 자유 비행 모드 OFF");
        }

        private void HandleMovement(Keyboard kb)
        {
            Vector3 move = Vector3.zero;

            // WASD: 수평 이동
            if (kb.wKey.isPressed) move += transform.forward;
            if (kb.sKey.isPressed) move -= transform.forward;
            if (kb.aKey.isPressed) move -= transform.right;
            if (kb.dKey.isPressed) move += transform.right;

            // Space/Shift: 상승/하강
            if (kb.spaceKey.isPressed) move += Vector3.up;
            if (kb.leftShiftKey.isPressed) move -= Vector3.up;

            if (move.sqrMagnitude > 0.01f)
            {
                transform.position += move.normalized * moveSpeed * Time.unscaledDeltaTime;
            }
        }

        private void HandleRotation(Keyboard kb)
        {
            float yawDelta = 0f;
            float pitchDelta = 0f;

            // Q/E: Yaw (좌우 회전) — C키는 FollowCamera 순환과 충돌하므로 E로 변경
            if (kb.qKey.isPressed) yawDelta -= rotateSpeed * Time.unscaledDeltaTime;
            if (kb.eKey.isPressed) yawDelta += rotateSpeed * Time.unscaledDeltaTime;

            // R/Z: Pitch (상하 회전)
            if (kb.rKey.isPressed) pitchDelta -= rotateSpeed * Time.unscaledDeltaTime;
            if (kb.zKey.isPressed) pitchDelta += rotateSpeed * Time.unscaledDeltaTime;

            // 마우스 우클릭 드래그로도 회전
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                yawDelta += delta.x * 0.3f;
                pitchDelta += delta.y * -0.3f;
            }

            _yaw += yawDelta;
            _pitch = Mathf.Clamp(_pitch + pitchDelta, -pitchLimit, pitchLimit);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void HandleSpeed()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                moveSpeed += scroll > 0 ? scrollSpeedStep : -scrollSpeedStep;
                moveSpeed = Mathf.Clamp(moveSpeed, minSpeed, maxSpeed);
            }
        }

        private void OnGUI()
        {
            if (!_isActive) return;

            // HUD 표시
            float x = Screen.width * 0.5f - 200f;
            float y = 10f;

            GUI.color = Color.yellow;
            GUI.Label(new Rect(x, y, 400f, 25f),
                $"<size=16><b>FREE FLY CAMERA</b> | Speed: {moveSpeed:F0}m/s</size>");
            y += 22f;
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, 400f, 20f),
                "<size=12>WASD: 이동 | Space/Shift: 상승/하강 | QE: 좌우회전 | RZ: 상하회전</size>");
            y += 18f;
            GUI.Label(new Rect(x, y, 400f, 20f),
                "<size=12>우클릭 드래그: 마우스 회전 | 스크롤: 속도 조절 | F/ESC: 종료</size>");
        }
    }
}
