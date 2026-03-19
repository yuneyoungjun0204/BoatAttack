using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 공격 에이전트: 모선에 접근하는 것이 목표
    /// ML 모드: 기본 모선 직진 위에 감속량 + 회피 조타를 학습
    /// Rush 모드: 규칙 기반 돌진 (기존 호환)
    /// </summary>
    public class AttackAgent : Agent, IHeuristicProvider
    {
        [Header("Boat Components")]
        [Tooltip("Boat 컴포넌트 (자동으로 찾음)")]
        private Boat _boat;

        [Tooltip("Engine 컴포넌트 (자동으로 찾음)")]
        private Engine _engine;

        [Header("Target Settings")]
        [Tooltip("공격 대상 모선 (태그로 찾거나 직접 할당)")]
        public GameObject targetMotherShip;

        [Tooltip("모선 태그 (targetMotherShip이 없을 때 사용)")]
        public string motherShipTag = "MotherShip";

        [Header("=== ML Attack Mode ===")]
        [Tooltip("ML 학습 모드 활성화 (false=기존 rush 규칙)")]
        public bool enableAttackML = false;

        [Tooltip("모선 거리 정규화 계수 (dist/(dist+k))")]
        public float distNormK = 500f;

        [Tooltip("Web 거리 정규화 계수")]
        public float webDistNormK = 200f;

        [Tooltip("최소 속도 비율 (rushThrottle 대비)")]
        [Range(0.3f, 0.9f)]
        public float minThrottleRatio = 0.5f;

        [Tooltip("최대 조타 오프셋 (기본 모선 직진 기준)")]
        [Range(0.1f, 1.5f)]
        public float maxSteeringOffset = 1.0f;

        [Header("ML Reward Settings")]
        [Tooltip("모선 접근 보상 계수 (dense)")]
        public float approachRewardCoeff = 0.01f;

        [Tooltip("시간 페널티 (dense, 매 스텝)")]
        public float mlTimePenalty = -0.0001f;

        [Tooltip("모선 도달 보상 (sparse)")]
        public float motherShipReachReward = 1.0f;

        [Tooltip("그물 포획 페널티 (sparse)")]
        public float webCapturePenalty = -1.0f;

        [Header("Observation Settings")]
        [Tooltip("Raycast로 주변 감지할 거리")]
        public float raycastDistance = 50f;

        [Tooltip("Raycast 방향 개수")]
        public int raycastCount = 8;

        [Header("Legacy Reward Settings")]
        [Tooltip("거리 기반 보상 계수 (가까울수록 높은 보상)")]
        public float distanceRewardMultiplier = 0.01f;

        [Tooltip("최대 보상 거리 (이 거리 이내면 보상)")]
        public float maxRewardDistance = 100f;

        [Tooltip("시간당 작은 페널티 (빠른 접근 유도)")]
        public float timePenalty = -0.001f;

        [Header("Control Settings")]
        [Range(0.1f, 2.0f)]
        [Tooltip("조종 감도 조절 (낮을수록 느림, 높을수록 빠름)")]
        public float steeringSensitivity = 0.3f;

        [Range(0.01f, 1.0f)]
        [Tooltip("입력 스무스 처리 속도 (낮을수록 더 부드러움, 높을수록 즉각 반응)")]
        public float inputSmoothing = 0.2f;

        [Header("Debug")]
        [Tooltip("에디터에서 Raycast 시각화")]
        public bool showRaycasts = true;

        [Header("Reward Display")]
        [Tooltip("현재 에피소드의 총 보상")]
        [SerializeField] private float _totalReward = 0f;

        [Header("Explosion Settings")]
        [Tooltip("폭발 효과 Prefab (War FX) - 자폭선박이 모선과 충돌 시 사용")]
        public GameObject explosionPrefab;

        [Tooltip("폭발 시 보상 (성공 보상)")]
        public float explosionReward = 50f;

        [Tooltip("폭발 효과 크기 배율")]
        [Range(5f, 50f)]
        public float explosionScale = 23f;

        [Header("Collision Detection")]
        [Tooltip("충돌 감지 방식: true=Trigger 사용, false=물리 Collision 사용")]
        public bool useTriggerCollision = false;

        [Header("Waypoint Following")]
        [Tooltip("Waypoint를 따라갈지 여부")]
        public bool followWaypoints = true;

        [Tooltip("Waypoint 추적 모드일 때 모선 접근 거리")]
        public float directChaseDistance = 50f;

        [Header("Rush Movement (모선 돌진)")]
        [Tooltip("돌진 모드 활성화 (followWaypoints=false 시 사용)")]
        public bool enableRush = true;

        [Tooltip("돌진 스로틀")]
        [Range(0.3f, 1.5f)]
        public float rushThrottle = 1.0f;

        [Tooltip("조향 노이즈 크기 (0~1, 클수록 불규칙)")]
        [Range(0f, 0.5f)]
        public float steeringNoise = 0.1f;

        [Tooltip("노이즈 변화 속도 (초당)")]
        public float noiseSpeed = 2f;

        // 내부 변수
        private float _noiseSeed;
        private float _lastDistance;
        private Vector3 _lastPosition;
        private float _episodeStartTime;
        private float _smoothThrottle = 0f;
        private float _smoothSteering = 0f;
        private bool _hasExploded = false;
        private float _prevThrottle = 0.75f;
        private float _prevSteering = 0f;

        // WebBufferSensor
        private BufferSensorComponent _webBufferSensorComp;

        // 활성 Web 캐시 (외부에서 설정)
        private List<DynamicWeb> _activeWebs = new List<DynamicWeb>();

        /// <summary>무력화 설정 (외부에서 호출: 그 자리에서 정지용)</summary>
        public void SetNeutralized() => _hasExploded = true;

        // Waypoint 관련 변수
        private int _currentWaypointIndex = 0;
        private Vector3 _currentWaypointPosition;
        private bool _waypointInitialized = false;

        /// <summary>활성 Web 목록 설정 (DefenseEnvController에서 호출)</summary>
        public void SetActiveWebs(List<DynamicWeb> webs)
        {
            _activeWebs = webs ?? new List<DynamicWeb>();
        }

        private new void Awake()
        {
            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
            }
            else
            {
                Debug.LogError($"[AttackAgent] Boat 컴포넌트를 찾을 수 없습니다. {gameObject.name}");
            }

            if (enableAttackML)
                EnsureMLComponents();
        }

        /// <summary>
        /// ML 학습에 필요한 컴포넌트를 자동으로 추가/설정
        /// (DecisionRequester, BufferSensorComponent)
        /// BehaviorParameters는 Inspector에서 설정 (코드에서 변경하면 초기화 타이밍 문제 발생)
        /// </summary>
        private void EnsureMLComponents()
        {
            // 1. DecisionRequester 추가 (없으면)
            var dr = GetComponent<DecisionRequester>();
            if (dr == null)
            {
                dr = gameObject.AddComponent<DecisionRequester>();
                dr.DecisionPeriod = 5;
                dr.TakeActionsBetweenDecisions = true;
            }

            // 2. BufferSensorComponent 추가 (없으면)
            _webBufferSensorComp = GetComponent<BufferSensorComponent>();
            if (_webBufferSensorComp == null)
            {
                _webBufferSensorComp = gameObject.AddComponent<BufferSensorComponent>();
                _webBufferSensorComp.SensorName = "WebBufferSensor";
                _webBufferSensorComp.ObservableSize = 3;
                _webBufferSensorComp.MaxNumObservables = 10;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _hasExploded = false;

            if (_engine == null && TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
            }

            if (targetMotherShip == null)
            {
                var ship = GameObject.FindGameObjectWithTag(motherShipTag);
                if (ship != null)
                    targetMotherShip = ship;
            }

            if (followWaypoints)
                InitializeWaypoint();

            // WebBufferSensor 초기화
            if (enableAttackML && _webBufferSensorComp == null)
            {
                _webBufferSensorComp = GetComponent<BufferSensorComponent>();
            }
        }

        public override void Initialize()
        {
            base.Initialize();

            // Awake에서 이미 추가했지만, 혹시 빠졌으면 재시도
            if (enableAttackML && _webBufferSensorComp == null)
            {
                _webBufferSensorComp = GetComponent<BufferSensorComponent>();
            }
        }

        public override void OnEpisodeBegin()
        {
            _episodeStartTime = Time.time;
            _totalReward = 0f;
            _smoothThrottle = 0f;
            _smoothSteering = 0f;
            _prevThrottle = 0.75f;
            _prevSteering = 0f;
            _noiseSeed = Random.Range(0f, 1000f);

            if (followWaypoints)
                InitializeWaypoint();

            if (_engine != null && _engine.RB != null)
            {
                _engine.RB.velocity = Vector3.zero;
                _engine.RB.angularVelocity = Vector3.zero;
            }

            _lastPosition = transform.position;

            if (targetMotherShip != null)
                _lastDistance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
        }

        // ===========================
        // ML 관측값
        // ===========================
        public override void CollectObservations(VectorSensor sensor)
        {
            if (enableAttackML)
            {
                CollectMLObservations(sensor);
                return;
            }
            CollectLegacyObservations(sensor);
        }

        /// <summary>
        /// ML 모드 관측값: VectorSensor(4) + WebBufferSensor(3/web)
        /// </summary>
        private void CollectMLObservations(VectorSensor sensor)
        {
            if (targetMotherShip == null || _engine == null)
            {
                sensor.AddObservation(0f); // 모선 거리
                sensor.AddObservation(0f); // 모선 상대각
                sensor.AddObservation(0f); // 이전 throttle
                sensor.AddObservation(0f); // 이전 steering
                FlushWebBufferEmpty();
                return;
            }

            Vector3 myPos = transform.position;
            Vector3 myFwd = transform.forward;
            Vector3 motherPos = targetMotherShip.transform.position;

            // 1. 모선 거리 (정규화)
            float dist = Vector3.Distance(myPos, motherPos);
            sensor.AddObservation(dist / (dist + distNormK)); // 0~1

            // 2. 모선 상대각 (자기 forward 기준)
            Vector3 toMother = motherPos - myPos;
            toMother.y = 0f;
            float angleToMother = Vector3.SignedAngle(myFwd, toMother.normalized, Vector3.up);
            sensor.AddObservation(angleToMother / 180f); // -1~1

            // 3. 이전 throttle
            sensor.AddObservation(_prevThrottle);

            // 4. 이전 steering
            sensor.AddObservation(_prevSteering);

            // WebBufferSensor: 활성 Web 정보
            CollectWebObservations();
        }

        /// <summary>
        /// WebBufferSensor에 각 Web의 (거리, 방향, 시야각) 입력
        /// </summary>
        private void CollectWebObservations()
        {
            if (_webBufferSensorComp == null) return;

            Vector3 myPos = transform.position;
            Vector3 myFwd = transform.forward;
            myFwd.y = 0f;
            myFwd.Normalize();

            foreach (var web in _activeWebs)
            {
                if (web == null) continue;
                if (web.defenseShip1 == null || web.defenseShip2 == null) continue;

                Vector3 ship1Pos = web.defenseShip1.position;
                Vector3 ship2Pos = web.defenseShip2.position;
                Vector3 webCenter = (ship1Pos + ship2Pos) * 0.5f;

                // 1. Web 중심 거리 (정규화)
                float webDist = Vector3.Distance(myPos, webCenter);
                float normDist = webDist / (webDist + webDistNormK);

                // 2. Web 중심 방향 (자기 forward 기준 상대각)
                Vector3 toWeb = webCenter - myPos;
                toWeb.y = 0f;
                float angleToWeb = 0f;
                if (toWeb.sqrMagnitude > 0.01f)
                    angleToWeb = Vector3.SignedAngle(myFwd, toWeb.normalized, Vector3.up);
                float normAngle = angleToWeb / 180f;

                // 3. Web 시야각 (subtended angle) — 그물이 시야에서 차지하는 폭
                float webWidth = Vector3.Distance(ship1Pos, ship2Pos);
                float subtendedAngle = 0f;
                if (webDist > 0.1f)
                    subtendedAngle = Mathf.Atan2(webWidth * 0.5f, webDist) * 2f * Mathf.Rad2Deg;
                float normSubtended = subtendedAngle / 180f;

                float[] obs = new float[] { normDist, normAngle, normSubtended };
                _webBufferSensorComp.AppendObservation(obs);
            }
        }

        private void FlushWebBufferEmpty()
        {
            // BufferSensor는 비어있으면 자동으로 0 패딩
        }

        /// <summary>기존 관측값 (레거시 호환)</summary>
        private void CollectLegacyObservations(VectorSensor sensor)
        {
            if (targetMotherShip == null || _engine == null)
            {
                for (int i = 0; i < GetObservationSize(); i++)
                    sensor.AddObservation(0f);
                return;
            }

            Vector3 toTarget = targetMotherShip.transform.position - transform.position;
            float distance = toTarget.magnitude;
            Vector3 direction = toTarget.normalized;

            sensor.AddObservation(distance / maxRewardDistance);
            sensor.AddObservation(direction.x);
            sensor.AddObservation(direction.z);

            float speed = _engine.RB.velocity.magnitude;
            sensor.AddObservation(Mathf.Clamp01(speed / 20f));

            Vector3 forward = transform.forward;
            sensor.AddObservation(forward.x);
            sensor.AddObservation(forward.z);

            float angleToTarget = Vector3.SignedAngle(transform.forward, direction, Vector3.up);
            sensor.AddObservation(angleToTarget / 180f);

            for (int i = 0; i < raycastCount; i++)
            {
                float angle = (360f / raycastCount) * i;
                Vector3 rayDirection = Quaternion.Euler(0, angle, 0) * transform.forward;

                RaycastHit hit;
                bool hasHit = Physics.Raycast(transform.position, rayDirection, out hit, raycastDistance);

                if (hasHit)
                {
                    sensor.AddObservation(1f - (hit.distance / raycastDistance));
                    sensor.AddObservation(hit.collider.CompareTag("boat") ? 1f : 0f);
                }
                else
                {
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                }
            }
        }

        // ===========================
        // ML 액션
        // ===========================
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _hasExploded) return;

            if (enableAttackML)
            {
                OnActionReceivedML(actions);
                return;
            }
            OnActionReceivedLegacy(actions);
        }

        /// <summary>
        /// ML 모드 액션: 기본 모선 직진 + ML 오버라이드 (감속 + 회피 조타)
        /// </summary>
        private void OnActionReceivedML(ActionBuffers actions)
        {
            _mlActionReceivedThisFrame = true;
            if (targetMotherShip == null) return;

            float throttleMod = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
            float steerOffset = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

            // 속도: [-1,1] → [minThrottleRatio, 1.0] × rushThrottle
            float range = 1f - minThrottleRatio;
            float finalThrottle = ((throttleMod + 1f) * 0.5f * range + minThrottleRatio) * rushThrottle;

            // 기본 조타: 모선 방향 직진
            Vector3 toMother = targetMotherShip.transform.position - transform.position;
            toMother.y = 0f;
            float angleToMother = Vector3.SignedAngle(transform.forward, toMother.normalized, Vector3.up);
            float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);

            // ML 오프셋 적용
            float finalSteering = Mathf.Clamp(baseSteering + steerOffset * maxSteeringOffset, -1f, 1f);

            // 엔진 제어
            _engine.Accelerate(finalThrottle);
            _engine.Turn(finalSteering * steeringSensitivity);

            _prevThrottle = finalThrottle;
            _prevSteering = finalSteering;

            // 보상 계산
            CalculateMLReward();
        }

        /// <summary>ML 보상 계산</summary>
        private void CalculateMLReward()
        {
            if (targetMotherShip == null) return;

            float currentDist = Vector3.Distance(transform.position, targetMotherShip.transform.position);

            // Dense: 모선 접근 보상 (거리 감소 → +, 거리 증가 → -)
            if (_lastDistance > 0f)
            {
                float deltaDist = _lastDistance - currentDist;
                AddReward(deltaDist * approachRewardCoeff);
            }

            // Dense: 시간 페널티
            AddReward(mlTimePenalty);

            _lastDistance = currentDist;
            _totalReward = GetCumulativeReward();
        }

        /// <summary>모선 도달 시 보상 (외부에서 호출)</summary>
        public void OnReachedMotherShip()
        {
            AddReward(motherShipReachReward);
            _totalReward = GetCumulativeReward();
        }

        /// <summary>그물에 포획 시 페널티 (외부에서 호출)</summary>
        public void OnCapturedByWeb()
        {
            AddReward(webCapturePenalty);
            _totalReward = GetCumulativeReward();
        }

        /// <summary>기존 액션 처리 (레거시)</summary>
        private void OnActionReceivedLegacy(ActionBuffers actions)
        {
            float rawThrottle;
            float rawSteering;

            // 돌진 모드 → FixedUpdate에서 처리
            if (!followWaypoints && enableRush && targetMotherShip != null)
                return;

            if (followWaypoints && _waypointInitialized)
            {
                rawThrottle = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
                rawSteering = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

                Vector3 toWaypoint = (_currentWaypointPosition - transform.position).normalized;
                float angleToWaypoint = Vector3.SignedAngle(transform.forward, toWaypoint, Vector3.up);
                float waypointSteeringHint = Mathf.Clamp(angleToWaypoint / 45f, -1f, 1f) * 0.3f;
                rawSteering = Mathf.Clamp(rawSteering + waypointSteeringHint, -1f, 1f);

                float distanceToWaypoint = Vector3.Distance(transform.position, _currentWaypointPosition);
                if (distanceToWaypoint < 10f)
                    AdvanceToNextWaypoint();
            }
            else
            {
                rawThrottle = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
                rawSteering = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
            }

            _smoothThrottle = Mathf.Lerp(_smoothThrottle, rawThrottle, inputSmoothing);
            _smoothSteering = Mathf.Lerp(_smoothSteering, rawSteering, inputSmoothing);

            float adjustedSteering = Mathf.Clamp(_smoothSteering * steeringSensitivity, -1f, 1f);

            _engine.Accelerate(_smoothThrottle);
            _engine.Turn(adjustedSteering);

            CalculateLegacyReward();
        }

        // ===========================
        // Heuristic
        // ===========================
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                continuousActions[0] = 0f;
                continuousActions[1] = 0f;
                return;
            }

            if (enableAttackML)
            {
                // ML 모드 Heuristic: throttleMod + steeringOffset
                // 기본값 (0, 0) = 0.75 속도로 모선 직진
                float throttle = 0f; // 기본 (0.75 속도)
                float steering = 0f; // 기본 (모선 직진)

                // W/S: 가감속 조절
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                    throttle = 1f;  // 전속력
                else if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                    throttle = -1f; // 절반 속도

                // A/D: 회피 조타
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    steering = -1f;
                else if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    steering = 1f;

                continuousActions[0] = throttle;
                continuousActions[1] = steering;
            }
            else
            {
                // 기존 Heuristic
                float throttle = 0f;
                float steering = 0f;

                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                    throttle = 1f;
                else if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                    throttle = -1f;

                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    steering = -1f;
                else if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    steering = 1f;

                continuousActions[0] = throttle;
                continuousActions[1] = steering;
            }
        }

        // ===========================
        // 기존 보상 (레거시)
        // ===========================
        private void CalculateLegacyReward()
        {
            if (targetMotherShip == null) return;

            float currentDistance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
            float rewardThisStep = 0f;

            if (currentDistance < _lastDistance)
            {
                float distanceImprovement = _lastDistance - currentDistance;
                rewardThisStep += distanceImprovement * distanceRewardMultiplier;
            }
            else if (currentDistance > _lastDistance)
            {
                float distanceWorsening = currentDistance - _lastDistance;
                rewardThisStep -= distanceWorsening * distanceRewardMultiplier * 0.5f;
            }

            if (currentDistance < maxRewardDistance)
            {
                float proximityReward = (maxRewardDistance - currentDistance) / maxRewardDistance;
                rewardThisStep += proximityReward * distanceRewardMultiplier * 0.1f;
            }

            rewardThisStep += timePenalty;

            AddReward(rewardThisStep);
            _totalReward = GetCumulativeReward();
            _lastDistance = currentDistance;
        }

        // ===========================
        // 기존 기능 (Waypoint, Rush, Explosion 등)
        // ===========================
        private void InitializeWaypoint()
        {
            if (WaypointGroup.Instance == null || WaypointGroup.Instance.WPs == null || WaypointGroup.Instance.WPs.Count == 0)
            {
                _waypointInitialized = false;
                return;
            }

            _currentWaypointIndex = 0;
            var firstWaypoint = WaypointGroup.Instance.WPs[0];
            _currentWaypointPosition = firstWaypoint.point;
            _waypointInitialized = true;
        }

        private void AdvanceToNextWaypoint()
        {
            if (WaypointGroup.Instance == null || WaypointGroup.Instance.WPs == null || WaypointGroup.Instance.WPs.Count == 0)
                return;

            _currentWaypointIndex++;
            if (_currentWaypointIndex >= WaypointGroup.Instance.WPs.Count)
                _currentWaypointIndex = 0;

            var waypoint = WaypointGroup.Instance.WPs[_currentWaypointIndex];
            _currentWaypointPosition = waypoint.point;
        }

        private int GetObservationSize()
        {
            if (enableAttackML) return 4;
            return 7 + (raycastCount * 2);
        }

        private Vector3 GetTargetDirection()
        {
            if (followWaypoints && _waypointInitialized)
            {
                if (targetMotherShip != null)
                {
                    float distanceToMotherShip = Vector3.Distance(transform.position, targetMotherShip.transform.position);
                    if (distanceToMotherShip < directChaseDistance)
                        return (targetMotherShip.transform.position - transform.position).normalized;
                }
                return (_currentWaypointPosition - transform.position).normalized;
            }
            else
            {
                if (targetMotherShip != null)
                    return (targetMotherShip.transform.position - transform.position).normalized;
            }
            return transform.forward;
        }

        // ===========================
        // FixedUpdate — Rush 모드 (ML 비활성 시)
        // ===========================
        // ML 모드에서 OnActionReceived가 이번 FixedUpdate에서 호출되었는지 추적
        private bool _mlActionReceivedThisFrame = false;

        private void FixedUpdate()
        {
            if (Time.frameCount % 300 == 1)
            {
                Debug.LogWarning($"[AttackAgent] {gameObject.name}: ML={enableAttackML}, followWP={followWaypoints}, rush={enableRush}, " +
                    $"mother={targetMotherShip != null}, engine={_engine != null}, exploded={_hasExploded}");
            }

            // ML 모드: OnActionReceived가 호출되지 않은 프레임에서는 기본 돌진 fallback
            if (enableAttackML)
            {
                if (!_mlActionReceivedThisFrame && targetMotherShip != null && _engine != null && !_hasExploded)
                {
                    // ML decision이 안 들어올 때 기본 돌진 (학습 프로세스 미연결 대비)
                    Vector3 toMother = targetMotherShip.transform.position - transform.position;
                    toMother.y = 0f;
                    float angleToMother = Vector3.SignedAngle(transform.forward, toMother.normalized, Vector3.up);
                    float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);
                    _engine.Accelerate(0.75f * rushThrottle);
                    _engine.Turn(baseSteering * steeringSensitivity);
                }
                _mlActionReceivedThisFrame = false;
                return;
            }

            // 기존 돌진 모드
            if (!followWaypoints && enableRush && targetMotherShip != null && _engine != null && !_hasExploded)
            {
                Vector3 toMother = targetMotherShip.transform.position - transform.position;
                toMother.y = 0f;

                float angleToMother = Vector3.SignedAngle(transform.forward, toMother.normalized, Vector3.up);
                float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);

                float noise = (Mathf.PerlinNoise(_noiseSeed, Time.time * noiseSpeed) - 0.5f) * 2f * steeringNoise;
                float steering = Mathf.Clamp(baseSteering + noise, -1f, 1f);

                _engine.Accelerate(rushThrottle);
                _engine.Turn(steering * steeringSensitivity);
            }
        }

        void Update()
        {
            CheckEpisodeEnd();
        }

        // ===========================
        // 충돌 / 폭발 처리
        // ===========================
        private void CheckEpisodeEnd()
        {
            if (_hasExploded || targetMotherShip == null) return;

            float distance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
            if (distance > 500f)
            {
                AddReward(-1f);
                EndEpisode();
            }

            if (Time.time - _episodeStartTime > 300f)
                EndEpisode();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!useTriggerCollision || _hasExploded) return;

            if (IsMotherShip(other.gameObject))
                HandleMotherShipCollision(other.gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_hasExploded) return;

            if (IsMotherShip(collision.gameObject))
            {
                HandleMotherShipCollision(collision.gameObject);
                return;
            }

            if (useTriggerCollision) return;
        }

        private bool IsMotherShip(GameObject obj)
        {
            if (obj == null) return false;
            if (!string.IsNullOrEmpty(motherShipTag) && obj.CompareTag(motherShipTag)) return true;
            if (targetMotherShip != null && obj == targetMotherShip) return true;
            if (obj.name.Contains("MotherShip") || obj.name.Contains("Mother")) return true;
            return false;
        }

        private void HandleMotherShipCollision(GameObject motherShip)
        {
            if (_hasExploded) return;

            TriggerExplosion();

            // ML 모드: 별도 보상
            if (enableAttackML)
                OnReachedMotherShip();
            else
                AddReward(explosionReward);

            StartCoroutine(EndEpisodeAfterDelay(1.5f));
        }

        private System.Collections.IEnumerator EndEpisodeAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            EndEpisode();
        }

        private void TriggerExplosion()
        {
            if (_hasExploded) return;
            _hasExploded = true;

            if (explosionPrefab == null) return;

            Vector3 explosionPosition = transform.position;
            explosionPosition.y += 0.5f;

            GameObject explosion = Instantiate(explosionPrefab, explosionPosition, Quaternion.identity);

            if (explosion != null)
            {
                explosion.SetActive(true);
                float scaleMultiplier = explosionScale;
                explosion.transform.localScale = Vector3.one * scaleMultiplier;

                ParticleSystem[] particleSystems = explosion.GetComponentsInChildren<ParticleSystem>();
                foreach (var ps in particleSystems)
                {
                    var main = ps.main;
                    if (main.startSize.mode == ParticleSystemCurveMode.Constant)
                        main.startSize = main.startSize.constant * scaleMultiplier;
                    else if (main.startSize.mode == ParticleSystemCurveMode.TwoConstants)
                        main.startSize = new ParticleSystem.MinMaxCurve(
                            main.startSize.constantMin * scaleMultiplier,
                            main.startSize.constantMax * scaleMultiplier);

                    if (main.startSpeed.mode == ParticleSystemCurveMode.Constant)
                        main.startSpeed = main.startSpeed.constant * scaleMultiplier;
                    else if (main.startSpeed.mode == ParticleSystemCurveMode.TwoConstants)
                        main.startSpeed = new ParticleSystem.MinMaxCurve(
                            main.startSpeed.constantMin * scaleMultiplier,
                            main.startSpeed.constantMax * scaleMultiplier);
                }
            }
        }

        // ===========================
        // Gizmos
        // ===========================
        private void OnDrawGizmos()
        {
            if (!showRaycasts || targetMotherShip == null) return;

            Gizmos.color = Color.red;
            Gizmos.DrawLine(transform.position, targetMotherShip.transform.position);

            if (!enableAttackML)
            {
                Gizmos.color = Color.yellow;
                for (int i = 0; i < raycastCount; i++)
                {
                    float angle = (360f / raycastCount) * i;
                    Vector3 rayDirection = Quaternion.Euler(0, angle, 0) * transform.forward;
                    Gizmos.DrawLine(transform.position, transform.position + rayDirection * raycastDistance);
                }
            }
        }
    }
}
