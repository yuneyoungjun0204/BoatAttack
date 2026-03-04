using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.Barracuda;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 방어 에이전트: 2대가 협력하여 적군 선박을 web 사이로 유도
    /// 상대 좌표 기반 관측, 보상은 DefenseEnvController에서 그룹 보상으로 분배
    /// </summary>
    public class DefenseAgent : Agent
    {
        [Header("Boat Components")]
        private Boat _boat;
        public Engine _engine;

        [Header("Observation Settings")]
        [Tooltip("관측할 적군 선박들 (인스펙터에서 할당)")]
        public GameObject[] enemyShips = new GameObject[5];

        [Tooltip("최대 관측 가능한 적군 수")]
        public int maxEnemyCount = 5;

        [Header("Target Settings")]
        public DefenseAgent partnerAgent;        public GameObject motherShip;
        public string motherShipTag = "MotherShip";
        public GameObject webObject;

        [Header("Hierarchical Control")]
        [Tooltip("배정된 타겟 인덱스 (-1=자동/가장 가까운 적, 1+=특정 적 1-indexed)")]
        [HideInInspector] public int assignedTargetIndex = -1;

        [Tooltip("환경 컨트롤러 참조 (Start에서 자동 설정)")]
        [HideInInspector] public DefenseEnvController envController;

        [Header("BufferSensor")]
        [Tooltip("적군 가변 관측용 BufferSensor (Inspector에서 할당)")]
        public BufferSensorComponent enemyBufferSensor;

        [Header("Action Settings")]
        public float maxLinearVelocity = 200f;
        public float maxAngularVelocity = 90f;
        public float velocityControlGain = 2.5f;
        public float angularVelocityControlGain = 1.0f;
        public float maxLinearAcceleration = 12f;
        public float maxAngularAcceleration = 45f;

        [Header("Control Settings")]
        [Range(0f, 1.5f)]
        [Tooltip("최대 Throttle (기본 전진 속도)")]
        public float maxThrottle = 1.0f;

        [Range(0f, 1f)]
        [Tooltip("최소 Throttle (action=0일 때 최소 전진력)")]
        public float minThrottle = 0.3f;

        [Range(0.1f, 2.0f)]
        public float steeringSensitivity = 1.0f;

        [Range(1f, 50f)]
        [Tooltip("아군 선박 horsePower 배율 (Engine.horsePower에 곱함)")]
        public float speedMultiplier = 10f;

        [Range(0.01f, 1.0f)]
        [Tooltip("입력 스무스 처리 (1.0 = 즉각 반응)")]
        public float inputSmoothing = 1.0f;

        [Header("Observation NormK (출력 0.5 지점 거리)")]
        [Range(1f, 1000f)] public float partnerNormK = 50f;
        [Range(1f, 1000f)] public float enemyNormK = 250f;
        [Range(1f, 1000f)] public float motherNormK = 500f;

        [Header("Observation Scale (정규화 후 가중치)")]
        [Range(0f, 5f)] public float partnerRScale = 1f;
        [Range(0f, 5f)] public float partnerFScale = 1f;
        [Range(0f, 5f)] public float partnerDistScale = 1f;
        [Range(0f, 5f)] public float partnerHdgScale = 1f;
        [Range(0f, 5f)] public float enemyRScale = 1f;
        [Range(0f, 5f)] public float enemyFScale = 1f;
        [Range(0f, 5f)] public float enemyDistScale = 1f;
        [Range(0f, 5f)] public float enemyHdgScale = 1f;
        [Range(0f, 5f)] public float motherRScale = 1f;
        [Range(0f, 5f)] public float motherFScale = 1f;
        [Range(0f, 5f)] public float motherDistScale = 1f;

        [Header("Ally Pair Observation (좌/우 가장 가까운 아군 쌍)")]
        [Range(1f, 1000f)] public float allyPairNormK = 100f;

        [Header("Heuristic")]
        [Tooltip("true면 화살표키, false면 WASD (페어 배치 시 자동 설정)")]
        public bool useArrowKeys = false;

        [Header("Debug")]
        public bool showRaycasts = true;
        public bool enableDebugLog = true;

        /// <summary>모니터링용: 마지막 CollectObservations 결과</summary>
        [HideInInspector] public float[] lastObservations;

        [Header("Reward Display")]
        #pragma warning disable CS0414
        [SerializeField] private float _totalReward = 0f;
        [SerializeField] private float _lastStepReward = 0f;
        #pragma warning restore CS0414

        private bool _episodeEnded = false;
        private bool _neutralized = false;
        private float _prevThrottle = 0f;
        private float _prevSteering = 0f;

        // 명령 변화량 (이전 스텝과의 차이, 보상 계산용)
        private float _throttleDelta = 0f;
        private float _steeringDelta = 0f;

        public float PrevThrottle => _prevThrottle;
        public float PrevSteering => _prevSteering;
        public float ThrottleDelta => _throttleDelta;
        public float SteeringDelta => _steeringDelta;

        private new void Awake()
        {
            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
                if (_engine != null && speedMultiplier > 1f)
                    _engine.horsePower *= speedMultiplier;
            }

            // BufferSensor 자동 찾기 → 없으면 AddComponent → 크기 보정
            // 코드에서 AppendObservation(new float[] { r, f, d, h }) → 4개 값
            if (enemyBufferSensor == null)
                enemyBufferSensor = GetComponent<BufferSensorComponent>();
            if (enemyBufferSensor == null)
                enemyBufferSensor = gameObject.AddComponent<BufferSensorComponent>();
            enemyBufferSensor.ObservableSize = 4;   // r, f, d, h
            enemyBufferSensor.MaxNumObservables = 10;
        }

        protected override void OnEnable()
        {
            // Stage4: DefenseAgent를 InferenceOnly로 전환 (base.OnEnable → LazyInitialize 전에 설정)
            if (envController != null && envController.IsCommanderStage()
                && envController.defenseOnnxModel != null)
            {
                var bp = GetComponent<BehaviorParameters>();
                if (bp != null)
                {
                    bp.BehaviorType = BehaviorType.InferenceOnly;
                    bp.Model = envController.defenseOnnxModel;
                }
            }

            base.OnEnable();

            if (motherShip == null)
            {
                // 멀티 환경 호환: 같은 환경 계층 내에서 먼저 찾기
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                var allWithTag = GameObject.FindGameObjectsWithTag(motherShipTag);
                foreach (var obj in allWithTag)
                {
                    if (obj != null && obj.transform.IsChildOf(envRoot))
                    {
                        motherShip = obj;
                        break;
                    }
                }
                // 환경 내에서 못 찾으면 글로벌 fallback
                if (motherShip == null && allWithTag.Length > 0)
                {
                    motherShip = allWithTag[0];
                }
            }
        }

        /// <summary>
        /// 무력화 상태 설정 (DisablePair에서 호출)
        /// true → OnActionReceived에서 엔진 구동 차단
        /// </summary>
        public void SetNeutralized(bool value) => _neutralized = value;

        /// <summary>
        /// 런타임 배치 시 에이전트 상태 리셋
        /// OnEpisodeBegin과 달리 ML-Agents 에피소드를 건드리지 않고 내부 플래그만 초기화
        /// </summary>
        public void ResetForDeployment()
        {
            _episodeEnded = false;
            _neutralized = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            assignedTargetIndex = -1; // Commander가 새로 배정
            // _neutralized는 여기서 리셋하지 않음
            // SetNeutralized(false)로만 해제 (DeployPairs/ResetScene에서 호출)
            _totalReward = 0f;
            _lastStepReward = 0f;
            _prevThrottle = 0f;
            _prevSteering = 0f;
            _throttleDelta = 0f;
            _steeringDelta = 0f;
        }

        /// <summary>
        /// 위치 정규화: x/(|x|+k) → (-1, 1)
        /// k = 감도 스케일 (출력 0.5 지점의 거리)
        /// </summary>
        private float NormalizePosition(float x, float k)
        {
            return x / (Mathf.Abs(x) + k);
        }

        /// <summary>
        /// 각도 정규화: sin(DeltaAngle) → [-1, 1]
        /// ±180° 부근 불연속 제거, ±90°에서 최대값
        /// </summary>
        private float NormalizeAngle(float fromAngle, float toAngle)
        {
            return Mathf.Sin(Mathf.DeltaAngle(fromAngle, toAngle) * Mathf.Deg2Rad);
        }

        /// <summary>
        /// 배정된 적군 반환 (Commander가 지정한 타겟 or 가장 가까운 적 fallback)
        /// 조건: 적군이 아군보다 모선에 더 가까워야 매칭 가능 (더 먼 적은 절대 매칭 불가)
        /// </summary>
        public GameObject GetAssignedEnemy()
        {
            // 배정된 타겟이 있으면 거리 무관하게 반환 (배정은 AutoAssign에서 주기적 갱신)
            if (assignedTargetIndex > 0 && enemyShips != null)
            {
                int idx = assignedTargetIndex - 1;
                if (idx < enemyShips.Length && enemyShips[idx] != null && enemyShips[idx].activeInHierarchy)
                {
                    return enemyShips[idx];
                }
                // 적이 비활성화된 경우에만 매칭 해제
                assignedTargetIndex = -1;
            }

            // Fallback: 가장 가까운 활성 적
            if (enemyShips == null) return null;
            float minDist = float.MaxValue;
            GameObject closest = null;
            Vector3 myPos = transform.position;
            foreach (var enemy in enemyShips)
            {
                if (enemy == null || !enemy.activeInHierarchy) continue;
                float dist = Vector3.Distance(myPos, enemy.transform.position);
                if (dist < minDist) { minDist = dist; closest = enemy; }
            }
            return closest;
        }

        /// <summary>
        /// 관측 수집 (VectorSensor 19개 + EnemyBufferSensor 가변)
        /// VectorSensor: 파트너(4) + 배정타겟(4) + 모선(3) + 좌측아군쌍(4) + 우측아군쌍(4) = 19
        /// BufferSensor: 나머지 적군 각 4개 (R, F, Dist, Hdg)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            const int VECTOR_OBS_COUNT = 19;  // 파트너4 + 타겟4 + 모선3 + 좌쌍4 + 우쌍4
            if (lastObservations == null || lastObservations.Length != VECTOR_OBS_COUNT)
                lastObservations = new float[VECTOR_OBS_COUNT];
            int oi = 0;

            if (_engine == null || _engine.RB == null)
            {
                for (int i = 0; i < VECTOR_OBS_COUNT; i++)
                {
                    sensor.AddObservation(0f);
                    lastObservations[i] = 0f;
                }
                return;
            }

            Vector3 myPos = transform.position;
            Vector3 myForward = transform.forward;
            Vector3 myRight = transform.right;
            float myAngle = transform.eulerAngles.y;

            // 1. 파트너 (4: R, F, Dist, Hdg) — 페어 내 고정 파트너
            if (partnerAgent != null && partnerAgent._engine != null && partnerAgent._engine.RB != null)
            {
                AddDirectionDistanceObs(sensor, partnerAgent.transform, myPos, myForward, myRight, myAngle,
                    partnerRScale, partnerFScale, partnerDistScale, partnerHdgScale, partnerNormK, ref oi);
            }
            else
            {
                for (int i = 0; i < 4; i++) AddObs(sensor, 0f, ref oi);
            }

            // 2. 배정 타겟 (4: R, F, Dist, Hdg) — Commander가 지정한 적 or 가장 가까운 적
            GameObject assignedEnemy = GetAssignedEnemy();
            if (assignedEnemy != null)
            {
                AddDirectionDistanceObs(sensor, assignedEnemy.transform, myPos, myForward, myRight, myAngle,
                    enemyRScale, enemyFScale, enemyDistScale, enemyHdgScale, enemyNormK, ref oi);
            }
            else
            {
                for (int i = 0; i < 4; i++) AddObs(sensor, 0f, ref oi);
            }

            // 3. 모선 (3: R, F, Dist)
            if (motherShip != null)
            {
                Vector3 rel = motherShip.transform.position - myPos;
                float dist = rel.magnitude;
                float rightDot = Vector3.Dot(rel, myRight);
                float fwdDot = Vector3.Dot(rel, myForward);

                if (dist > 0.1f)
                {
                    AddObs(sensor, (rightDot / dist) * motherRScale, ref oi);
                    AddObs(sensor, (fwdDot / dist) * motherFScale, ref oi);
                }
                else
                {
                    AddObs(sensor, 0f, ref oi);
                    AddObs(sensor, 0f, ref oi);
                }
                AddObs(sensor, NormalizePosition(dist, motherNormK) * motherDistScale, ref oi);
            }
            else
            {
                for (int i = 0; i < 3; i++) AddObs(sensor, 0f, ref oi);
            }

            // 4. 좌/우 가장 가까운 아군 쌍 (각 4: dist, fwd, right, hdg)
            CollectNearbyAllyPairObs(sensor, myPos, myForward, myRight, myAngle, ref oi);

            // 5. BufferSensor: 나머지 적군 (배정 타겟 제외, 가변)
            if (enemyBufferSensor != null && enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;
                    if (enemy == assignedEnemy) continue; // 배정 타겟은 VectorSensor에서 이미 관측

                    Vector3 rel = enemy.transform.position - myPos;
                    float dist = rel.magnitude;
                    float rightDot = Vector3.Dot(rel, myRight);
                    float fwdDot = Vector3.Dot(rel, myForward);

                    float r = (dist > 0.1f) ? (rightDot / dist) * enemyRScale : 0f;
                    float f = (dist > 0.1f) ? (fwdDot / dist) * enemyFScale : 0f;
                    float d = NormalizePosition(dist, enemyNormK) * enemyDistScale;
                    float h = NormalizeAngle(myAngle, enemy.transform.eulerAngles.y) * enemyHdgScale;

                    enemyBufferSensor.AppendObservation(new float[] { r, f, d, h });
                }
            }
        }

        /// <summary>대상의 방향(R,F) + 거리 + 헤딩차이를 VectorSensor에 추가 (4개)</summary>
        private void AddDirectionDistanceObs(VectorSensor sensor, Transform target,
            Vector3 myPos, Vector3 myForward, Vector3 myRight, float myAngle,
            float rScale, float fScale, float distScale, float hdgScale, float normK, ref int oi)
        {
            Vector3 rel = target.position - myPos;
            float dist = rel.magnitude;
            float rightDot = Vector3.Dot(rel, myRight);
            float fwdDot = Vector3.Dot(rel, myForward);

            if (dist > 0.1f)
            {
                AddObs(sensor, (rightDot / dist) * rScale, ref oi);
                AddObs(sensor, (fwdDot / dist) * fScale, ref oi);
            }
            else
            {
                AddObs(sensor, 0f, ref oi);
                AddObs(sensor, 0f, ref oi);
            }
            AddObs(sensor, NormalizePosition(dist, normK) * distScale, ref oi);
            AddObs(sensor, NormalizeAngle(myAngle, target.eulerAngles.y) * hdgScale, ref oi);
        }

        /// <summary>
        /// 좌/우 가장 가까운 아군 쌍 관측 수집 (8개: 좌4 + 우4)
        /// 내 heading 기준 좌측/우측에서 가장 가까운 다른 쌍의 중심점 정보
        /// 해당 방향에 쌍이 없으면 (1.0, 0, 0, 0) = "매우 멀고 방향 없음"
        /// </summary>
        private void CollectNearbyAllyPairObs(VectorSensor sensor, Vector3 myPos,
            Vector3 myForward, Vector3 myRight, float myAngle, ref int oi)
        {
            float leftMinDist = float.MaxValue;
            float rightMinDist = float.MaxValue;
            Vector3 leftCenter = Vector3.zero;
            Vector3 rightCenter = Vector3.zero;
            float leftHeading = 0f;
            float rightHeading = 0f;
            bool hasLeft = false, hasRight = false;

            var lzm = envController != null ? envController.launchZoneManager : null;
            if (lzm != null && lzm.IsInitialized)
            {
                int myPairIdx = lzm.FindPairIndex(this);
                int poolCount = lzm.GetCurrentPoolCount();

                for (int i = 0; i < poolCount; i++)
                {
                    if (i == myPairIdx) continue;
                    DefensePair pair = lzm.GetPair(i);
                    if (pair == null || !pair.isActive) continue;
                    if (pair.agent1 == null || pair.agent2 == null) continue;

                    // 다른 쌍의 중심점
                    Vector3 otherCenter = (pair.agent1.transform.position + pair.agent2.transform.position) * 0.5f;
                    Vector3 toOther = otherCenter - myPos;
                    float dist = toOther.magnitude;

                    // 좌/우 판별: myRight와의 내적
                    float rightDot = Vector3.Dot(myRight, toOther);

                    // 쌍의 평균 헤딩
                    float otherHeading = (pair.agent1.transform.eulerAngles.y + pair.agent2.transform.eulerAngles.y) * 0.5f;

                    if (rightDot >= 0f) // 우측
                    {
                        if (dist < rightMinDist)
                        {
                            rightMinDist = dist;
                            rightCenter = otherCenter;
                            rightHeading = otherHeading;
                            hasRight = true;
                        }
                    }
                    else // 좌측
                    {
                        if (dist < leftMinDist)
                        {
                            leftMinDist = dist;
                            leftCenter = otherCenter;
                            leftHeading = otherHeading;
                            hasLeft = true;
                        }
                    }
                }
            }

            // 좌측 쌍 (4개)
            if (hasLeft)
            {
                Vector3 rel = leftCenter - myPos;
                float dist = rel.magnitude;
                float fwdDot = Vector3.Dot(rel, myForward);
                float rDot = Vector3.Dot(rel, myRight);

                AddObs(sensor, dist / (dist + allyPairNormK), ref oi);
                AddObs(sensor, dist > 0.1f ? fwdDot / dist : 0f, ref oi);
                AddObs(sensor, dist > 0.1f ? rDot / dist : 0f, ref oi);
                AddObs(sensor, NormalizeAngle(myAngle, leftHeading), ref oi);
            }
            else
            {
                AddObs(sensor, 1f, ref oi);  // 거리 = 1.0 (매우 멀다)
                AddObs(sensor, 0f, ref oi);  // 전방 성분 없음
                AddObs(sensor, 0f, ref oi);  // 측면 성분 없음
                AddObs(sensor, 0f, ref oi);  // 헤딩 차이 없음
            }

            // 우측 쌍 (4개)
            if (hasRight)
            {
                Vector3 rel = rightCenter - myPos;
                float dist = rel.magnitude;
                float fwdDot = Vector3.Dot(rel, myForward);
                float rDot = Vector3.Dot(rel, myRight);

                AddObs(sensor, dist / (dist + allyPairNormK), ref oi);
                AddObs(sensor, dist > 0.1f ? fwdDot / dist : 0f, ref oi);
                AddObs(sensor, dist > 0.1f ? rDot / dist : 0f, ref oi);
                AddObs(sensor, NormalizeAngle(myAngle, rightHeading), ref oi);
            }
            else
            {
                AddObs(sensor, 1f, ref oi);
                AddObs(sensor, 0f, ref oi);
                AddObs(sensor, 0f, ref oi);
                AddObs(sensor, 0f, ref oi);
            }
        }

        /// <summary>관측값 기록 + 센서 추가 헬퍼</summary>
        private void AddObs(VectorSensor sensor, float value, ref int index)
        {
            sensor.AddObservation(value);
            if (lastObservations != null && index < lastObservations.Length)
                lastObservations[index++] = value;
        }

        /// <summary>ㅊ
        /// 액션 수신: actions[0]=throttle(-1~1), actions[1]=steering(-1~1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _engine.RB == null || _episodeEnded || _neutralized)
            {
                Debug.LogWarning($"[{name}] OnAction BLOCKED: engine={_engine != null}, RB={_engine?.RB != null}, ended={_episodeEnded}, neutral={_neutralized}");
                return;
            }

            float throttleInput = actions.ContinuousActions[0];
            float steeringInput = actions.ContinuousActions[1];

            // NaN 방지
            if (float.IsNaN(throttleInput) || float.IsInfinity(throttleInput)) throttleInput = 0f;
            if (float.IsNaN(steeringInput) || float.IsInfinity(steeringInput)) steeringInput = 0f;

            throttleInput = Mathf.Clamp(throttleInput, -1f, 1f);
            steeringInput = Mathf.Clamp(steeringInput, -1f, 1f);

            // Throttle Mapping (simple):
            //   action [-1,+1] → throttle [minThrottle, maxThrottle]
            //   -1 → minThrottle(0.3), 0 → mid(0.65), +1 → maxThrottle(1.0)
            float throttle = minThrottle + (throttleInput + 1f) * 0.5f * (maxThrottle - minThrottle);

            // Steering
            float steering = Mathf.Clamp(steeringInput * steeringSensitivity, -1f, 1f);

            // Smoothing
            if (inputSmoothing < 1f)
            {
                throttle = Mathf.Lerp(_prevThrottle, throttle, inputSmoothing);
                steering = Mathf.Lerp(_prevSteering, steering, inputSmoothing);
            }
            _throttleDelta = Mathf.Abs(throttle - _prevThrottle);
            _steeringDelta = Mathf.Abs(steering - _prevSteering);

            _prevThrottle = throttle;
            _prevSteering = steering;

            _engine.Accelerate(throttle);
            _engine.Turn(steering);

            // 디버그 로그
            if (enableDebugLog)
            {
                Debug.Log($"[{gameObject.name}] Throttle: {throttle:F2}, Steering: {steering:F2}");
            }
        }

        /// <summary>
        /// Web-적군 충돌 처리 → DefenseEnvController로 전달
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (_episodeEnded)
                return;

            _episodeEnded = true;

            // 멀티 환경 호환: 같은 환경 계층 내에서 컨트롤러 찾기
            Transform envRoot = transform.parent != null ? transform.parent : transform;
            DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
            if (envController != null)
            {
                envController.OnEnemyCaptured(enemyPosition);
            }
        }

        /// <summary>
        /// 수동 조종 - agent1: WASD, agent2: 화살표키
        /// </summary>
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

            float throttle = 0f;
            float steering = 0f;

            if (useArrowKeys)
            {
                // Agent2: 화살표키
                if (keyboard.upArrowKey.isPressed) throttle = 1f;
                else if (keyboard.downArrowKey.isPressed) throttle = -0.5f;
                if (keyboard.rightArrowKey.isPressed) steering = 1f;
                else if (keyboard.leftArrowKey.isPressed) steering = -1f;
            }
            else
            {
                // Agent1: WASD
                if (keyboard.wKey.isPressed) throttle = 1f;
                else if (keyboard.sKey.isPressed) throttle = -0.5f;
                if (keyboard.dKey.isPressed) steering = 1f;
                else if (keyboard.aKey.isPressed) steering = -1f;
            }

            continuousActions[0] = Mathf.Clamp(throttle, -1f, 1f);
            continuousActions[1] = Mathf.Clamp(steering, -1f, 1f);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_episodeEnded || _neutralized)
                return;

            if (collision.gameObject.GetComponent<DefenseAgent>() != null ||
                collision.gameObject.CompareTag("MotherShip"))
            {
                // 멀티 환경 호환: 같은 환경 계층 내에서 컨트롤러 찾기
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                if (envController != null)
                {
                    envController.OnFriendlyCollision(this);
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;

            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, 2f);

            if (partnerAgent != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, partnerAgent.transform.position);
            }

            if (enemyShips != null)
            {
                Gizmos.color = Color.red;
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null)
                        Gizmos.DrawLine(transform.position, enemy.transform.position);
                }
            }

            if (webObject != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(webObject.transform.position, 3f);
            }
        }
    }
}
