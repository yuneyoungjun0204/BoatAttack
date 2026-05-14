using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

namespace BoatAttack
{
    /// <summary>
    /// 공격 에이전트: 모선에 접근하는 것이 목표
    /// 보상: 모선과의 거리가 가까울수록 보상
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
        
        [Header("Observation Settings")]
        [Tooltip("Raycast로 주변 감지할 거리")]
        public float raycastDistance = 50f;
        
        [Tooltip("Raycast 방향 개수")]
        public int raycastCount = 8;
        
        [Header("Reward Settings")]
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
        [Tooltip("충돌 감지 방식: true=Trigger 사용 (보트 Collider의 Is Trigger 활성화 필요), false=물리 Collision 사용")]
        public bool useTriggerCollision = false;
        
        [Header("Waypoint Following")]
        [Tooltip("Waypoint를 따라갈지 여부 (true면 waypoint 경로를 따라가고, false면 모선을 직접 추적)")]
        public bool followWaypoints = true;
        
        [Tooltip("Waypoint 추적 모드일 때 모선 접근 거리 (이 거리 이내면 모선을 직접 추적)")]
        public float directChaseDistance = 50f;

        [Header("=== Self-Play 모드 ===")]
        [Tooltip("Self-Play 학습 모드 (true: ML이 미세 조정, false: 기존 스크립트)")]
        public bool selfPlayMode = true;

        [Tooltip("Self-Play 시 throttle 오프셋 범위 (0~이 값, 감속만)")]
        [Range(0f, 0.5f)]
        public float selfPlayThrottleRange = 0.6f;

        [Tooltip("Self-Play 시 steering 오프셋 범위")]
        [Range(0f, 0.5f)]
        public float selfPlaySteeringRange = 0.4f;

        [Tooltip("Self-Play 시 Web 근접 페널티 시작 거리 (m)")]
        public float webAvoidanceThreshold = 40f;

        [Tooltip("Self-Play 시 Web 근접 페널티 계수")]
        public float webAvoidancePenalty = -0.002f;

        [Tooltip("Self-Play 시 포획당했을 때 페널티")]
        public float capturedPenalty = -5.0f;

        [Tooltip("Self-Play 시 모선 도달 보상")]
        public float motherShipReachReward = 5.0f;

        /// <summary>Self-Play 관측 수 (모선거리, 모선각도, Web거리, Web방위)</summary>
        private const int SELF_PLAY_OBS_COUNT = 4;

        /// <summary>EnvController 참조 (Self-Play용)</summary>
        private DefenseEnvController _envController;

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

        [Header("Island Avoidance")]
        [Tooltip("섬 감지 레이어 마스크 (Inspector에서 Island 레이어 선택)")]
        public LayerMask islandLayerMask = 0;
        [Tooltip("섬 감지 전방 거리 (m)")]
        [Range(20f, 200f)]
        public float islandDetectRange = 80f;
        [Tooltip("회피 조향 강도 (1=완전 덮어씀)")]
        [Range(0f, 1f)]
        public float islandAvoidStrength = 0.9f;

        // 각 적군마다 다른 노이즈 시드
        private float _noiseSeed;

        private float _lastDistance;
        private Vector3 _lastPosition;
        private float _episodeStartTime;
        
        // 스무스 입력을 위한 변수
        private float _smoothThrottle = 0f;
        private float _smoothSteering = 0f;
        
        // 폭발 관련 변수
        private bool _hasExploded = false;

        /// <summary>무력화 설정 (외부에서 호출: 그 자리에서 정지용)</summary>
        public void SetNeutralized() => _hasExploded = true;
        
        // Waypoint 추적 관련 변수 (AiController 참고)
        private int _currentWaypointIndex = 0;
        private Vector3 _currentWaypointPosition;
        private bool _waypointInitialized = false;

        private new void Awake()
        {
            // Boat와 Engine 컴포넌트 찾기
            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
            }
            else
            {
                Debug.LogError($"[AttackAgent] Boat 컴포넌트를 찾을 수 없습니다. {gameObject.name}");
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _hasExploded = false; // 풀 재사용 시 폭발 상태 초기화

            // Engine 재확인 (비활성→활성 전환 시 Awake에서 못 잡은 경우)
            if (_engine == null && TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
            }

            // 모선 찾기
            if (targetMotherShip == null)
            {
                var ship = GameObject.FindGameObjectWithTag(motherShipTag);
                if (ship != null)
                {
                    targetMotherShip = ship;
                }
                else
                {
                    Debug.LogWarning($"[AttackAgent] 모선을 찾을 수 없습니다. 태그: {motherShipTag}");
                }
            }
            
            // Waypoint 초기화
            if (followWaypoints)
            {
                InitializeWaypoint();
            }
        }
        
        /// <summary>
        /// Waypoint 초기화 (첫 waypoint 할당)
        /// </summary>
        private void InitializeWaypoint()
        {
            if (WaypointGroup.Instance == null)
            {
                Debug.LogWarning("[AttackAgent] WaypointGroup.Instance가 null입니다. Waypoint를 따라갈 수 없습니다.");
                _waypointInitialized = false;
                return;
            }
            
            if (WaypointGroup.Instance.WPs == null || WaypointGroup.Instance.WPs.Count == 0)
            {
                Debug.LogWarning("[AttackAgent] Waypoint가 없습니다. Waypoint를 따라갈 수 없습니다.");
                _waypointInitialized = false;
                return;
            }
            
            // 첫 waypoint 할당
            _currentWaypointIndex = 0;
            var firstWaypoint = WaypointGroup.Instance.WPs[0];
            _currentWaypointPosition = firstWaypoint.point;
            _waypointInitialized = true;
            
            Debug.Log($"[AttackAgent] Waypoint 초기화 완료: 첫 waypoint = {_currentWaypointPosition}");
        }

        public override void Initialize()
        {
            base.Initialize();
            if (selfPlayMode)
            {
                // TeamId 자동 설정
                var bp = GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (bp != null)
                {
                    bp.TeamId = 1; // 적군 팀
                    bp.BehaviorName = "Attack";
                }

                // DecisionRequester 자동 추가
                var dr = GetComponent<Unity.MLAgents.DecisionRequester>();
                if (dr == null)
                {
                    dr = gameObject.AddComponent<Unity.MLAgents.DecisionRequester>();
                    dr.DecisionPeriod = 5;
                    dr.TakeActionsBetweenDecisions = true;
                }

                // EnvController 찾기
                _envController = GetComponentInParent<DefenseEnvController>();
                if (_envController == null)
                    _envController = FindObjectOfType<DefenseEnvController>();
            }
        }

        public override void OnEpisodeBegin()
        {
            // 에피소드 시작 시 초기화
            _episodeStartTime = Time.time;
            _totalReward = 0f; // 총 보상 초기화
            _smoothThrottle = 0f; // 스무스 입력 초기화
            _smoothSteering = 0f;
            // _hasExploded는 여기서 리셋하지 않음
            // SetNeutralized() 후 OnEpisodeBegin 자동 호출 시 다시 움직이는 것 방지
            // 풀 재활성화 시 OnEnable()에서 리셋됨
            _noiseSeed = Random.Range(0f, 1000f); // 각 적군마다 다른 노이즈 패턴
            
            // Waypoint 초기화 (에피소드 재시작 시)
            if (followWaypoints)
            {
                InitializeWaypoint();
            }
            
            // 배 위치는 AttackBoatManager에서 재생성 시 설정되므로 여기서는 리셋하지 않음
            // 단, Rigidbody 속도 및 각속도만 리셋
            if (_engine != null && _engine.RB != null)
            {
                _engine.RB.velocity = Vector3.zero;
                _engine.RB.angularVelocity = Vector3.zero;
            }
            
            _lastPosition = transform.position;
            
            if (targetMotherShip != null)
            {
                _lastDistance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
            }
            
            Debug.Log($"[AttackAgent] OnEpisodeBegin: 배 위치 = {transform.position}");
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            // === Self-Play 모드: 간소화된 4개 관측 ===
            if (selfPlayMode)
            {
                CollectSelfPlayObservations(sensor);
                return;
            }

            if (targetMotherShip == null || _engine == null)
            {
                // 관측 불가능한 경우 0으로 채움
                for (int i = 0; i < GetObservationSize(); i++)
                {
                    sensor.AddObservation(0f);
                }
                return;
            }

            // 1. 모선과의 상대 위치 (정규화)
            Vector3 toTarget = targetMotherShip.transform.position - transform.position;
            float distance = toTarget.magnitude;
            Vector3 direction = toTarget.normalized;

            sensor.AddObservation(distance / maxRewardDistance); // 거리 (0~1)
            sensor.AddObservation(direction.x); // 방향 X
            sensor.AddObservation(direction.z); // 방향 Z (Y는 무시)

            // 2. 자신의 속도 (정규화)
            float speed = _engine.RB.velocity.magnitude;
            sensor.AddObservation(Mathf.Clamp01(speed / 20f)); // 최대 속도 20 가정

            // 3. 자신의 방향 (forward 벡터)
            Vector3 forward = transform.forward;
            sensor.AddObservation(forward.x);
            sensor.AddObservation(forward.z);

            // 4. 모선 방향으로의 각도 (정규화)
            float angleToTarget = Vector3.SignedAngle(transform.forward, direction, Vector3.up);
            sensor.AddObservation(angleToTarget / 180f); // -1 ~ 1

            // 5. Raycast로 주변 장애물 감지
            for (int i = 0; i < raycastCount; i++)
            {
                float angle = (360f / raycastCount) * i;
                Vector3 rayDirection = Quaternion.Euler(0, angle, 0) * transform.forward;

                RaycastHit hit;
                bool hasHit = Physics.Raycast(transform.position, rayDirection, out hit, raycastDistance);

                if (hasHit)
                {
                    sensor.AddObservation(1f - (hit.distance / raycastDistance)); // 거리 (0~1, 가까울수록 1)
                    // 장애물 타입 구분 (선택적)
                    sensor.AddObservation(hit.collider.CompareTag("boat") ? 1f : 0f);
                }
                else
                {
                    sensor.AddObservation(0f); // 장애물 없음
                    sensor.AddObservation(0f);
                }
            }
        }

        /// <summary>
        /// Self-Play 관측: 모선(거리, 각도) + 가장 가까운 Web(거리, 방위) = 4개
        /// </summary>
        private void CollectSelfPlayObservations(VectorSensor sensor)
        {
            Vector3 myPos = transform.position;
            Vector3 myForward = transform.forward;

            // 1. 모선 거리 (정규화: dist/(dist+k))
            float motherDist = 0f;
            float motherAngle = 0f;
            if (targetMotherShip != null)
            {
                Vector3 toMother = targetMotherShip.transform.position - myPos;
                toMother.y = 0f;
                motherDist = toMother.magnitude;
                motherAngle = Vector3.SignedAngle(myForward, toMother, Vector3.up) / 180f; // -1~+1
            }
            float normK = 200f;
            sensor.AddObservation(motherDist / (motherDist + normK)); // 0~1
            sensor.AddObservation(motherAngle);                       // -1~+1

            // 2. 가장 가까운 Web(아군 쌍) 거리 + 방위
            float nearestWebDist = 1f; // 정규화된 최대값 (멀리 = 안전)
            float nearestWebBrg = 0f;
            if (_envController != null && _envController.launchZoneManager != null
                && _envController.launchZoneManager.IsInitialized)
            {
                float bestDist = float.MaxValue;
                int poolCount = _envController.launchZoneManager.GetCurrentPoolCount();
                for (int i = 0; i < poolCount; i++)
                {
                    var pair = _envController.launchZoneManager.GetPair(i);
                    if (pair == null || !pair.isActive) continue;
                    if (pair.agent1 == null) continue;

                    // Web 중심: 두 에이전트의 중간점
                    Vector3 webCenter = pair.agent1.transform.position;
                    if (pair.agent2 != null)
                        webCenter = (webCenter + pair.agent2.transform.position) * 0.5f;

                    float dist = Vector3.Distance(myPos, webCenter);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        Vector3 toWeb = webCenter - myPos;
                        toWeb.y = 0f;
                        nearestWebBrg = Vector3.SignedAngle(myForward, toWeb, Vector3.up) / 180f;
                    }
                }
                if (bestDist < float.MaxValue)
                    nearestWebDist = bestDist / (bestDist + normK);
            }
            sensor.AddObservation(nearestWebDist);  // 0~1 (가까울수록 0)
            sensor.AddObservation(nearestWebBrg);    // -1~+1
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _hasExploded)
            {
                return;
            }

            // === Self-Play 모드: 자동 조향 + ML 오프셋 ===
            if (selfPlayMode)
            {
                OnActionReceivedSelfPlay(actions);
                return;
            }

            float rawThrottle;
            float rawSteering;

            // 돌진 모드 → FixedUpdate에서 처리, 여기서는 스킵
            if (!followWaypoints && enableRush && targetMotherShip != null)
            {
                return;
            }
            // 기존 Waypoint 추적 모드
            else if (followWaypoints && _waypointInitialized)
            {
                rawThrottle = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
                rawSteering = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

                Vector3 toWaypoint = (_currentWaypointPosition - transform.position).normalized;
                float angleToWaypoint = Vector3.SignedAngle(transform.forward, toWaypoint, Vector3.up);
                float waypointSteeringHint = Mathf.Clamp(angleToWaypoint / 45f, -1f, 1f) * 0.3f;
                rawSteering = Mathf.Clamp(rawSteering + waypointSteeringHint, -1f, 1f);

                float distanceToWaypoint = Vector3.Distance(transform.position, _currentWaypointPosition);
                if (distanceToWaypoint < 10f)
                {
                    AdvanceToNextWaypoint();
                }
            }
            // 기본 모드 (ML-Agents 액션 그대로)
            else
            {
                rawThrottle = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
                rawSteering = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
            }

            // 스무스 처리
            _smoothThrottle = Mathf.Lerp(_smoothThrottle, rawThrottle, inputSmoothing);
            _smoothSteering = Mathf.Lerp(_smoothSteering, rawSteering, inputSmoothing);

            // 감도 조절 적용
            float adjustedSteering = Mathf.Clamp(_smoothSteering * steeringSensitivity, -1f, 1f);

            // Engine에 전달
            _engine.Accelerate(_smoothThrottle);
            _engine.Turn(adjustedSteering);

            // 보상 계산
            CalculateReward();
        }
        
        /// <summary>
        /// 현재 목표 방향 가져오기 (waypoint 또는 모선)
        /// </summary>
        private Vector3 GetTargetDirection()
        {
            if (followWaypoints && _waypointInitialized)
            {
                // 모선과의 거리가 가까우면 모선을 직접 추적
                if (targetMotherShip != null)
                {
                    float distanceToMotherShip = Vector3.Distance(transform.position, targetMotherShip.transform.position);
                    if (distanceToMotherShip < directChaseDistance)
                    {
                        return (targetMotherShip.transform.position - transform.position).normalized;
                    }
                }
                
                // 그 외에는 waypoint를 따라감
                return (_currentWaypointPosition - transform.position).normalized;
            }
            else
            {
                // Waypoint 추적 모드가 아니면 모선을 직접 추적
                if (targetMotherShip != null)
                {
                    return (targetMotherShip.transform.position - transform.position).normalized;
                }
            }
            
            return transform.forward;
        }
        
        /// <summary>
        /// 다음 waypoint로 진행
        /// </summary>
        private void AdvanceToNextWaypoint()
        {
            if (WaypointGroup.Instance == null || WaypointGroup.Instance.WPs == null || WaypointGroup.Instance.WPs.Count == 0)
            {
                return;
            }
            
            _currentWaypointIndex++;
            if (_currentWaypointIndex >= WaypointGroup.Instance.WPs.Count)
            {
                // 마지막 waypoint에 도달하면 첫 번째로 돌아감 (루프)
                _currentWaypointIndex = 0;
            }
            
            var waypoint = WaypointGroup.Instance.WPs[_currentWaypointIndex];
            _currentWaypointPosition = waypoint.point;
            
            Debug.Log($"[AttackAgent] 다음 waypoint로 진행: {_currentWaypointIndex} = {_currentWaypointPosition}");
        }

        private void CalculateReward()
        {
            if (targetMotherShip == null)
            {
                return;
            }

            // 거리 기반 보상
            float currentDistance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
            float rewardThisStep = 0f;
            
            // 거리가 가까워졌으면 보상
            if (currentDistance < _lastDistance)
            {
                float distanceImprovement = _lastDistance - currentDistance;
                rewardThisStep += distanceImprovement * distanceRewardMultiplier;
            }
            else if (currentDistance > _lastDistance)
            {
                // 거리가 멀어지면 작은 페널티
                float distanceWorsening = currentDistance - _lastDistance;
                rewardThisStep -= distanceWorsening * distanceRewardMultiplier * 0.5f;
            }

            // 최대 보상 거리 이내에 있으면 추가 보상
            if (currentDistance < maxRewardDistance)
            {
                float proximityReward = (maxRewardDistance - currentDistance) / maxRewardDistance;
                rewardThisStep += proximityReward * distanceRewardMultiplier * 0.1f;
            }

            // 시간 페널티 (빠른 접근 유도)
            rewardThisStep += timePenalty;

            // 보상 추가 및 총 보상 업데이트
            AddReward(rewardThisStep);
            _totalReward = GetCumulativeReward(); // ML-Agents의 총 보상 가져오기

            _lastDistance = currentDistance;
        }

        /// <summary>
        /// Self-Play 모드 액션 처리: 자동 조향 + ML 오프셋
        /// </summary>
        private void OnActionReceivedSelfPlay(ActionBuffers actions)
        {
            // 기본: 모선 방향으로 최대 속도 직진
            float baseThrottle = 1.0f;
            float baseSteering = 0f;

            if (targetMotherShip != null)
            {
                Vector3 toMother = targetMotherShip.transform.position - transform.position;
                toMother.y = 0f;
                float angleToMother = Vector3.SignedAngle(transform.forward, toMother, Vector3.up);
                baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);
            }

            // ML 오프셋: 감속 + 좌우 미세 조정
            float throttleOffset = actions.ContinuousActions[0] * selfPlayThrottleRange; // -0.3~+0.3
            float steeringOffset = actions.ContinuousActions[1] * selfPlaySteeringRange; // -0.2~+0.2

            float finalThrottle = Mathf.Clamp(baseThrottle + throttleOffset, 0.5f, 1.0f);
            float finalSteering = Mathf.Clamp(baseSteering + steeringOffset, -1f, 1f);

            // 섬 회피: 감지 시 조향값 강제 덮어씀
            float islandAvoid = ComputeIslandAvoidanceSteering();
            if (Mathf.Abs(islandAvoid) > 0.01f)
                finalSteering = Mathf.Lerp(finalSteering, islandAvoid, islandAvoidStrength);

            _engine.Accelerate(finalThrottle);
            _engine.Turn(finalSteering);

            // Self-Play 보상 계산
            CalculateSelfPlayReward();
        }

        /// <summary>
        /// Self-Play 보상: Δdist 접근 + Web 근접 페널티 + 시간 페널티
        /// </summary>
        private void CalculateSelfPlayReward()
        {
            if (targetMotherShip == null) return;

            float currentDistance = Vector3.Distance(transform.position, targetMotherShip.transform.position);
            float reward = 0f;

            // 1. 모선 접근 보상 (Δdist)
            float deltaD = _lastDistance - currentDistance; // 양수 = 접근
            reward += deltaD * distanceRewardMultiplier;

            // 2. 시간 페널티
            reward += timePenalty;

            // 3. Web 근접 페널티: 가장 가까운 Web이 threshold 이내면 페널티
            if (_envController != null && _envController.launchZoneManager != null
                && _envController.launchZoneManager.IsInitialized && webAvoidanceThreshold > 0f)
            {
                float bestDist = float.MaxValue;
                int poolCount = _envController.launchZoneManager.GetCurrentPoolCount();
                for (int i = 0; i < poolCount; i++)
                {
                    var pair = _envController.launchZoneManager.GetPair(i);
                    if (pair == null || !pair.isActive || pair.agent1 == null) continue;

                    Vector3 webCenter = pair.agent1.transform.position;
                    if (pair.agent2 != null)
                        webCenter = (webCenter + pair.agent2.transform.position) * 0.5f;

                    float dist = Vector3.Distance(transform.position, webCenter);
                    if (dist < bestDist) bestDist = dist;
                }
                if (bestDist < webAvoidanceThreshold)
                {
                    float proximity = 1f - bestDist / webAvoidanceThreshold; // 0~1
                    reward += webAvoidancePenalty * proximity;
                }
            }

            AddReward(reward);
            _totalReward = GetCumulativeReward();
            _lastDistance = currentDistance;
        }

        /// <summary>
        /// Self-Play: 포획당했을 때 외부에서 호출
        /// </summary>
        public void OnCapturedBySelfPlay()
        {
            if (!selfPlayMode) return;
            AddReward(capturedPenalty);
        }

        /// <summary>
        /// Self-Play: 모선 도달 시 외부에서 호출
        /// </summary>
        public void OnReachedMotherShipSelfPlay()
        {
            if (!selfPlayMode) return;
            AddReward(motherShipReachReward);
        }

        /// <summary>
        /// 관측 크기 계산 (디버깅용)
        /// </summary>
        private int GetObservationSize()
        {
            if (selfPlayMode) return SELF_PLAY_OBS_COUNT;
            // 모선 정보: 5 (거리, 방향x2, 각도, 속도)
            // 자신 정보: 2 (방향x2)
            // Raycast: raycastCount * 2 (거리, 타입)
            return 7 + (raycastCount * 2);
        }

        /// <summary>
        /// Heuristic 함수: 키보드로 수동 테스트
        /// ML-Agents Behavior Type을 "Heuristic Only"로 설정하면 이 함수가 호출됩니다.
        /// Unity의 새로운 Input System (UnityEngine.InputSystem)을 사용합니다.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;
            
            // Unity Input System 사용 (Legacy Input 대신)
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                // 키보드가 없으면 0으로 설정
                continuousActions[0] = 0f;
                continuousActions[1] = 0f;
                return;
            }

            float throttle = 0f;
            float steering = 0f;

            // 가속/감속 (W/S 또는 위/아래 화살표)
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
            {
                throttle = 1f;
            }
            else if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
            {
                throttle = -1f;
            }

            // 좌우 회전 (A/D 또는 왼쪽/오른쪽 화살표)
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                steering = -1f;
            }
            else if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                steering = 1f;
            }

            // Action에 키보드 입력 전달
            continuousActions[0] = throttle;
            continuousActions[1] = steering;
        }

        /// <summary>
        /// 에디터에서 Raycast 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!showRaycasts || targetMotherShip == null)
            {
                return;
            }

            // 모선 방향 표시
            Gizmos.color = Color.red;
            if (targetMotherShip != null)
            {
                Gizmos.DrawLine(transform.position, targetMotherShip.transform.position);
            }

            // Raycast 방향 표시
            Gizmos.color = Color.yellow;
            for (int i = 0; i < raycastCount; i++)
            {
                float angle = (360f / raycastCount) * i;
                Vector3 rayDirection = Quaternion.Euler(0, angle, 0) * transform.forward;
                Vector3 endPoint = transform.position + rayDirection * raycastDistance;
                
                Gizmos.DrawLine(transform.position, endPoint);
            }
        }

        /// <summary>
        /// 에피소드 종료 조건 (선택적)
        /// </summary>
        private void CheckEpisodeEnd()
        {
            if (_hasExploded)
            {
                return;
            }

            if (targetMotherShip == null)
            {
                return;
            }

            float distance = Vector3.Distance(transform.position, targetMotherShip.transform.position);

            // 너무 멀어지면 실패
            if (distance > 500f)
            {
                AddReward(-1f); // 페널티
                EndEpisode();
            }

            // 시간 초과 (선택적)
            if (Time.time - _episodeStartTime > 300f) // 5분
            {
                EndEpisode();
            }
        }

        /// <summary>
        /// Trigger 충돌 감지 (IsTrigger가 활성화된 Collider)
        /// </summary>
        private void OnTriggerEnter(Collider other)
        {
            if (!useTriggerCollision || _hasExploded)
            {
                return;
            }
            
            Debug.Log($"[AttackAgent] OnTriggerEnter 호출: {other.gameObject.name}, Tag: {other.tag}");
            
            // 충돌한 객체가 MotherShip인지 확인
            bool isMotherShip = IsMotherShip(other.gameObject);
            Debug.Log($"[AttackAgent] IsMotherShip 확인 결과: {isMotherShip}");
            
            if (isMotherShip)
            {
                Debug.Log("[AttackAgent] MotherShip 충돌 감지! 폭발 처리 시작");
                HandleMotherShipCollision(other.gameObject);
            }
            else
            {
                Debug.Log($"[AttackAgent] MotherShip이 아님. Tag: {other.tag}, Name: {other.gameObject.name}");
            }
        }

        /// <summary>
        /// Collision 충돌 감지 (물리 충돌)
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            Debug.Log($"[AttackAgent] OnCollisionEnter 호출: {collision.gameObject.name}, Tag: {collision.gameObject.tag}, useTriggerCollision: {useTriggerCollision}, _hasExploded: {_hasExploded}");
            
            if (_hasExploded)
            {
                Debug.Log("[AttackAgent] 이미 폭발했으므로 무시");
                return;
            }

            // 충돌한 객체가 MotherShip인지 먼저 확인
            bool isMotherShip = IsMotherShip(collision.gameObject);
            Debug.Log($"[AttackAgent] IsMotherShip 확인 결과: {isMotherShip}");
            
            // MotherShip과의 충돌은 useTriggerCollision 설정과 관계없이 항상 처리
            if (isMotherShip)
            {
                Debug.Log("[AttackAgent] MotherShip 충돌 감지! 폭발 처리 시작 (useTriggerCollision 설정 무시)");
                HandleMotherShipCollision(collision.gameObject);
                return;
            }
            
            // MotherShip이 아닌 경우, useTriggerCollision이 true면 무시
            if (useTriggerCollision)
            {
                Debug.Log("[AttackAgent] useTriggerCollision이 true이고 MotherShip이 아니므로 Collision 무시");
                return;
            }
            
            Debug.Log($"[AttackAgent] 일반 충돌: {collision.gameObject.name} (처리하지 않음)");
        }

        /// <summary>
        /// 충돌한 객체가 MotherShip인지 확인
        /// </summary>
        private bool IsMotherShip(GameObject obj)
        {
            if (obj == null)
            {
                Debug.LogWarning("[AttackAgent] IsMotherShip: obj가 null입니다");
                return false;
            }

            // 태그로 확인
            if (!string.IsNullOrEmpty(motherShipTag) && obj.CompareTag(motherShipTag))
            {
                Debug.Log($"[AttackAgent] IsMotherShip: 태그로 확인됨 ({motherShipTag})");
                return true;
            }

            // 직접 할당된 targetMotherShip과 비교
            if (targetMotherShip != null && obj == targetMotherShip)
            {
                Debug.Log("[AttackAgent] IsMotherShip: targetMotherShip과 일치");
                return true;
            }

            // 이름으로 확인 (선택적)
            if (obj.name.Contains("MotherShip") || obj.name.Contains("Mother"))
            {
                Debug.Log($"[AttackAgent] IsMotherShip: 이름으로 확인됨 ({obj.name})");
                return true;
            }

            Debug.Log($"[AttackAgent] IsMotherShip: 일치하지 않음. Tag: {obj.tag}, Name: {obj.name}, motherShipTag: {motherShipTag}");
            return false;
        }

        /// <summary>
        /// MotherShip과의 충돌 처리
        /// </summary>
        private void HandleMotherShipCollision(GameObject motherShip)
        {
            if (_hasExploded)
            {
                Debug.LogWarning("[AttackAgent] HandleMotherShipCollision: 이미 폭발했음");
                return;
            }

            Debug.Log($"[AttackAgent] HandleMotherShipCollision 호출! 폭발 처리 시작. MotherShip: {motherShip.name}");
            
            // 폭발 처리 (explosionPrefab이 없어도 진행)
            TriggerExplosion();
            
            // 보상 추가
            AddReward(explosionReward);
            Debug.Log($"[AttackAgent] 보상 추가: {explosionReward}, 총 보상: {GetCumulativeReward()}");
            
            // 폭발 효과가 보이도록 약간의 딜레이 후 에피소드 종료
            StartCoroutine(EndEpisodeAfterDelay(1.5f));
        }
        
        /// <summary>
        /// 딜레이 후 에피소드 종료 (폭발 효과가 보이도록)
        /// </summary>
        private System.Collections.IEnumerator EndEpisodeAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            Debug.Log("[AttackAgent] 에피소드 종료");
            EndEpisode();
        }

        /// <summary>
        /// 폭발 효과 생성 및 처리
        /// </summary>
        private void TriggerExplosion()
        {
            if (_hasExploded)
            {
                Debug.LogWarning("[AttackAgent] TriggerExplosion: 이미 폭발했음");
                return;
            }

            Debug.Log($"[AttackAgent] TriggerExplosion 호출! 폭발 효과 생성 위치: {transform.position}");
            _hasExploded = true;

            // 폭발 효과 생성 검증
            if (explosionPrefab == null)
            {
                Debug.LogError("[AttackAgent] TriggerExplosion: explosionPrefab이 null입니다! Inspector에서 War FX 폭발 효과 Prefab을 할당해주세요.");
                Debug.LogError("[AttackAgent] 경로 예시: Assets/JMO Assets/WarFX/_Effects/Explosions/WFX_Explosion.prefab");
                return;
            }

            // 폭발 효과 생성 위치 (선박 위치, Y축은 약간 위로)
            Vector3 explosionPosition = transform.position;
            explosionPosition.y += 0.5f; // 폭발 효과가 선박 위에 보이도록
            
            // War FX 폭발 효과 생성
            GameObject explosion = Instantiate(explosionPrefab, explosionPosition, Quaternion.identity);
            
            // 폭발 효과 활성화 확인
            if (explosion != null)
            {
                explosion.SetActive(true);
                
                // 폭발 효과 크기 조정 (100 = 1.0배, 200 = 2.0배, 500 = 5.0배)
                float scaleMultiplier = explosionScale;
                explosion.transform.localScale = Vector3.one * scaleMultiplier;
                
                // 모든 ParticleSystem의 크기와 속도도 조정 (더 큰 폭발 효과)
                ParticleSystem[] particleSystems = explosion.GetComponentsInChildren<ParticleSystem>();
                foreach (var ps in particleSystems)
                {
                    var main = ps.main;
                    // Start Size 증가
                    if (main.startSize.mode == ParticleSystemCurveMode.Constant)
                    {
                        main.startSize = main.startSize.constant * scaleMultiplier;
                    }
                    else if (main.startSize.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        main.startSize = new ParticleSystem.MinMaxCurve(
                            main.startSize.constantMin * scaleMultiplier,
                            main.startSize.constantMax * scaleMultiplier
                        );
                    }
                    
                    // Start Speed 증가 (폭발이 더 멀리 퍼지도록)
                    if (main.startSpeed.mode == ParticleSystemCurveMode.Constant)
                    {
                        main.startSpeed = main.startSpeed.constant * scaleMultiplier;
                    }
                    else if (main.startSpeed.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        main.startSpeed = new ParticleSystem.MinMaxCurve(
                            main.startSpeed.constantMin * scaleMultiplier,
                            main.startSpeed.constantMax * scaleMultiplier
                        );
                    }
                }
                
                Debug.Log($"[AttackAgent] 폭발 효과 생성 완료!");
                Debug.Log($"[AttackAgent] - 이름: {explosion.name}");
                Debug.Log($"[AttackAgent] - 위치: {explosionPosition}");
                Debug.Log($"[AttackAgent] - 크기 배율: {explosionScale}% ({scaleMultiplier}x)");
                Debug.Log($"[AttackAgent] - 활성화 상태: {explosion.activeSelf}");
                Debug.Log($"[AttackAgent] - ParticleSystem 개수: {particleSystems.Length}");
                
                if (particleSystems.Length == 0)
                {
                    Debug.LogWarning("[AttackAgent] 폭발 효과에 ParticleSystem이 없습니다! Prefab이 올바른지 확인하세요.");
                }
            }
            else
            {
                Debug.LogError("[AttackAgent] 폭발 효과 생성 실패! Instantiate가 null을 반환했습니다.");
            }
        }

        /// <summary>
        /// 섬 장애물 회피 조향값 계산.
        /// 전방/좌전방/우전방 Raycast로 Island 레이어 감지 → [-1, 1] 조향 보정값 반환.
        /// 감지 없으면 0f.
        /// </summary>
        private float ComputeIslandAvoidanceSteering()
        {
            if (islandLayerMask == 0) return 0f;

            float avoidSteering = 0f;
            Vector3 origin = transform.position + Vector3.up * 2f;

            // 전방, 좌45°, 우45° 세 방향 체크
            (Vector3 dir, float sign)[] rays = {
                (transform.forward,                                   0f),  // 정면 → 좌우 판단 별도
                (Quaternion.Euler(0, -45f, 0) * transform.forward,  1f),  // 좌전방 → 우측 회피
                (Quaternion.Euler(0,  45f, 0) * transform.forward, -1f),  // 우전방 → 좌측 회피
            };

            for (int i = 0; i < rays.Length; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, rays[i].dir, out hit, islandDetectRange, islandLayerMask))
                    continue;

                float urgency = 1f - (hit.distance / islandDetectRange);  // 가까울수록 1

                if (i == 0)
                {
                    // 정면 감지: 좌/우 중 빈 쪽으로
                    bool leftClear = !Physics.Raycast(origin,
                        Quaternion.Euler(0, -90f, 0) * transform.forward,
                        islandDetectRange * 0.5f, islandLayerMask);
                    avoidSteering += urgency * (leftClear ? -1f : 1f);
                }
                else
                {
                    avoidSteering += urgency * rays[i].sign;
                }
            }

            return Mathf.Clamp(avoidSteering, -1f, 1f);
        }

        private void FixedUpdate()
        {
            // 진단 로그 (첫 5프레임만)
            if (Time.frameCount % 300 == 1)
            {
                Debug.LogWarning($"[AttackAgent] {gameObject.name}: followWP={followWaypoints}, rush={enableRush}, " +
                    $"mother={targetMotherShip != null}, engine={_engine != null}, exploded={_hasExploded}");
            }

            // 돌진 모드: 모선 향해 풀스로틀 + Perlin 노이즈로 약간의 불규칙성
            if (!followWaypoints && enableRush && targetMotherShip != null && _engine != null && !_hasExploded)
            {
                Vector3 toMother = targetMotherShip.transform.position - transform.position;
                toMother.y = 0f;

                float angleToMother = Vector3.SignedAngle(transform.forward, toMother.normalized, Vector3.up);
                float baseSteering = Mathf.Clamp(angleToMother / 45f, -1f, 1f);

                // Perlin 노이즈로 부드러운 랜덤 조향 (각 적군마다 다른 패턴)
                float noise = (Mathf.PerlinNoise(_noiseSeed, Time.time * noiseSpeed) - 0.5f) * 2f * steeringNoise;

                float steering = Mathf.Clamp(baseSteering + noise, -1f, 1f);

                // 섬 회피
                float islandAvoid = ComputeIslandAvoidanceSteering();
                if (Mathf.Abs(islandAvoid) > 0.01f)
                    steering = Mathf.Lerp(steering, islandAvoid, islandAvoidStrength);

                _engine.Accelerate(rushThrottle);
                _engine.Turn(steering * steeringSensitivity);
            }
        }

        void Update()
        {
            // 에피소드 종료 조건 체크
            CheckEpisodeEnd();
        }
    }
}
