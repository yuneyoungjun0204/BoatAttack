using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BoatAttack
{
    /// <summary>
    /// 전장 통합 뷰용 라이브 추적 카메라.
    /// 실제 씬의 아군(DefenseAgent) 또는 적군(AttackAgent) 선박을 따라가며
    /// 전용 Camera→RenderTexture→RawImage로 PiP 창에 송출한다.
    /// ShipPreviewRenderer(복제 프리뷰)와 달리 실제 플레이 중인 선박을 추적한다.
    /// </summary>
    public class BattleViewCamera : MonoBehaviour
    {
        public enum ViewMode { Ally, Enemy, BirdsEye }

        [Header("=== References ===")]
        public RawImage targetImage;
        public DefenseEnvController envController;

        [Header("=== Mode ===")]
        public ViewMode mode = ViewMode.Ally;

        [Tooltip("Enemy 모드 전용: 이 아군 뷰가 현재 보는 아군이 '타겟하는 적'을 추적한다")]
        public BattleViewCamera linkedAllyView;

        [Header("=== BirdsEye 모드 (모선 정수직 부감) ===")]
        [Tooltip("추적 대상 위 고도(m). 높을수록 넓게 내려다봄")] public float birdsEyeAltitude = 450f;

        [Header("=== RenderTexture ===")]
        public int textureWidth = 640;
        public int textureHeight = 360;
        public Color background = new Color(0.05f, 0.08f, 0.14f, 1f);

        [Header("=== Chase Camera ===")]
        [Tooltip("대상 뒤쪽 거리(m)")] public float distance = 60f;
        [Tooltip("대상 위 높이(m)")] public float height = 20f;
        [Tooltip("시야각")] public float fov = 58.5f;
        [Tooltip("추적 부드러움 (클수록 빠름)")] public float followLerp = 5f;

        [Header("=== Targeting ===")]
        [Tooltip("대상 재탐색 간격(초)")] public float refreshInterval = 0.5f;
        [Tooltip("수동 대상 순환 키 (None=자동만)")] public KeyCode cycleKey = KeyCode.None;

        Camera _cam;
        RenderTexture _rt;
        Transform _target;
        int _index;
        float _scanTimer;
        bool _ready;

        public Transform CurrentTarget => _target;

        void OnEnable()
        {
            EnsureCamera();
            if (_cam != null) _cam.enabled = true;
        }

        void OnDisable()
        {
            if (_cam != null) _cam.enabled = false;
        }

        void OnDestroy() => Cleanup();

        void EnsureCamera()
        {
            if (_ready)
            {
                if (targetImage != null && _rt != null) targetImage.texture = _rt;
                return;
            }

            _rt = new RenderTexture(Mathf.Max(64, textureWidth), Mathf.Max(64, textureHeight), 24)
            {
                name = $"BattleRT_{mode}",
                antiAliasing = 2
            };
            _rt.Create();

            var camObj = new GameObject($"BattleCam_{mode}");
            _cam = camObj.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.Skybox;
            _cam.backgroundColor = background;
            _cam.fieldOfView = fov;
            _cam.nearClipPlane = 0.3f;
            _cam.farClipPlane = 8000f;
            _cam.depth = -5;            // 메인 카메라보다 먼저 렌더
            _cam.cullingMask = -1;      // 모든 레이어
            TryAddURPCameraData(camObj);

            if (targetImage != null) targetImage.texture = _rt;
            _ready = true;
        }

        /// <summary>URP 프로젝트에서 카메라 데이터 컴포넌트를 리플렉션으로 추가(직접 종속 회피)</summary>
        static void TryAddURPCameraData(GameObject camObj)
        {
            var urpType = System.Type.GetType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (urpType != null && camObj.GetComponent(urpType) == null)
                camObj.AddComponent(urpType);
        }

        void LateUpdate()
        {
            if (_cam == null) return;

            if (cycleKey != KeyCode.None && Input.GetKeyDown(cycleKey))
                CycleNext();

            _scanTimer -= Time.unscaledDeltaTime;
            if (_target == null || _scanTimer <= 0f)
            {
                _scanTimer = Mathf.Max(0.1f, refreshInterval);
                AcquireTarget();
            }

            float t = 1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime);

            if (mode == ViewMode.BirdsEye)
            {
                PlaceBirdsEye(t);
                return;
            }

            if (_target == null) return;

            // 추격 카메라 (Ally / Enemy)
            Vector3 fwd = _target.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 desired = _target.position - fwd * distance + Vector3.up * height;
            _cam.transform.position = Vector3.Lerp(_cam.transform.position, desired, t);
            _cam.transform.LookAt(_target.position + Vector3.up * 2f);
        }

        /// <summary>모선에 가장 가까운 선박을 정수직 상공에서 추적하는 버드아이뷰.</summary>
        void PlaceBirdsEye(float t)
        {
            // 추적 대상: 모선 최근접 선박(_target), 없으면 모선
            Vector3 center;
            if (_target != null)
                center = _target.position;
            else if (envController != null && envController.motherShip != null)
                center = envController.motherShip.transform.position;
            else
                center = Vector3.zero;

            Vector3 desired = center + Vector3.up * birdsEyeAltitude;
            _cam.transform.position = Vector3.Lerp(_cam.transform.position, desired, t);
            // 정북 기준 수직 하강 (위에서 아래로 쫙)
            _cam.transform.rotation = Quaternion.Slerp(
                _cam.transform.rotation, Quaternion.Euler(90f, 0f, 0f), t);
        }

        public void CycleNext()
        {
            var list = GetCandidates();
            if (list.Count == 0) { _target = null; return; }
            _index = (_index + 1) % list.Count;
            _target = list[_index];
        }

        void AcquireTarget()
        {
            var list = GetCandidates();
            if (list.Count == 0) { _target = null; return; }
            if (_index >= list.Count) _index = 0;
            _target = list[_index];
        }

        /// <summary>현재 추적 후보 목록(관련도 순 정렬).</summary>
        List<Transform> GetCandidates()
        {
            var result = new List<Transform>();

            // BirdsEye: 모선에 가장 가까운 선박을 추적 (활성 적군 + 배정된 아군, 미할당 아군 제외)
            if (mode == ViewMode.BirdsEye)
            {
                Vector3 motherPos = (envController != null && envController.motherShip != null)
                    ? envController.motherShip.transform.position : Vector3.zero;

                // 활성 적군
                if (envController != null && envController.enemyShips != null)
                    foreach (var e in envController.enemyShips)
                        if (e != null && e.activeInHierarchy) result.Add(e.transform);

                // 배정된 아군만 (assignedTargetIndex > 0)
                foreach (var a in Object.FindObjectsOfType<DefenseAgent>())
                    if (a != null && a.gameObject.activeInHierarchy && a.assignedTargetIndex > 0)
                        result.Add(a.transform);

                // 모선에 가장 가까운 순 정렬
                if (result.Count > 1 && motherPos != Vector3.zero)
                    result.Sort((x, y) =>
                        (x.position - motherPos).sqrMagnitude.CompareTo((y.position - motherPos).sqrMagnitude));

                return result;
            }

            if (mode == ViewMode.Ally)
            {
                var lzm = envController != null ? envController.GetComponentInChildren<LaunchZoneManager>() : null;
                if (lzm != null && lzm.IsInitialized)
                {
                    foreach (var a in lzm.GetActiveAgents())
                        if (a != null && a.gameObject.activeInHierarchy) result.Add(a.transform);
                }
                if (result.Count == 0)
                {
                    foreach (var a in Object.FindObjectsOfType<DefenseAgent>())
                        if (a != null && a.gameObject.activeInHierarchy) result.Add(a.transform);
                }
            }
            else // Enemy: '아군이 타겟하는 실제 적군'만 추적 (마젠타 템플릿 배제)
            {
                var seen = new HashSet<Transform>();

                // 1순위: 아군 카메라가 현재 보는 아군이 타겟하는 적
                if (linkedAllyView != null && linkedAllyView.CurrentTarget != null)
                {
                    var shownAlly = linkedAllyView.CurrentTarget.GetComponent<DefenseAgent>();
                    if (shownAlly != null)
                    {
                        var e = shownAlly.GetAssignedEnemy();
                        if (e != null && e.activeInHierarchy && seen.Add(e.transform))
                            result.Add(e.transform);
                    }
                }

                // 2순위: 그 외 활성 아군들이 타겟하는 적 (순환용 후보)
                foreach (var a in Object.FindObjectsOfType<DefenseAgent>())
                {
                    if (a == null || !a.gameObject.activeInHierarchy) continue;
                    var e = a.GetAssignedEnemy();
                    if (e != null && e.activeInHierarchy && seen.Add(e.transform))
                        result.Add(e.transform);
                }
                // 1순위 항목이 index 0이므로 자동 선택 시 '보는 아군의 타겟'이 잡힌다
            }
            return result;
        }

        void Cleanup()
        {
            if (_cam != null) { Destroy(_cam.gameObject); _cam = null; }
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            _ready = false;
        }
    }
}
