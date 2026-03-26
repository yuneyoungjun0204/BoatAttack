using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
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
        public DefenseAgent partnerAgent;
        public GameObject motherShip;
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
        [Tooltip("최소 Throttle (감속 시 최소값)")]
        public float minThrottle = 0.8f;

        [Range(0.1f, 2.0f)]
        public float steeringSensitivity = 0.3f;

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

        [Header("=== LOS Guidance (비교군) ===")]
        [Tooltip("LOS 가이던스 ON: 모델 추론 무시, 규칙 기반 추종")]
        public bool useLOSGuidance = true;

        [Tooltip("그물 전개를 위한 좌우 벌림 거리 (m)")]
        [Range(5f, 100f)] public float losSpreadDistance = 30f;

        [Tooltip("비례 조향 이득 (bearing → steering)")]
        [Range(0.5f, 10f)] public float losSteeringGain = 3f;

        [Tooltip("이 거리 이내에서 감속 시작 (m)")]
        [Range(10f, 500f)] public float losApproachDist = 150f;

        [Tooltip("LOS 최소 속력")]
        [Range(0f, 1f)] public float losThrottleMin = 0.3f;

        [Tooltip("LOS 최대 속력")]
        [Range(0.5f, 1.5f)] public float losThrottleMax = 1.0f;

        [Tooltip("agent1이면 true (왼쪽 offset), agent2면 false (오른쪽)")]
        public bool isLeftAgent = true;

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
            }
        }

        protected override void OnEnable()
        {
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

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
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
        /// </summary>
        public GameObject GetAssignedEnemy()
        {
            // Commander 배정: 1-indexed
            if (assignedTargetIndex > 0 && enemyShips != null)
            {
                int idx = assignedTargetIndex - 1;
                if (idx < enemyShips.Length && enemyShips[idx] != null && enemyShips[idx].activeInHierarchy)
                    return enemyShips[idx];
            }

            // Fallback: 가장 가까운 활성 적군
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
        /// 관측 수집 (VectorSensor 11개 + EnemyBufferSensor 가변)
        /// VectorSensor: 파트너(4) + 배정타겟(4) + 모선(3) = 11
        /// BufferSensor: 나머지 적군 각 4개 (R, F, Dist, Hdg)
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            const int VECTOR_OBS_COUNT = 11;  // 파트너4 + 타겟4 + 모선3
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

            // 4. BufferSensor: 나머지 적군 (배정 타겟 제외, 가변)
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

        /// <summary>관측값 기록 + 센서 추가 헬퍼</summary>
        private void AddObs(VectorSensor sensor, float value, ref int index)
        {
            sensor.AddObservation(value);
            if (lastObservations != null && index < lastObservations.Length)
                lastObservations[index++] = value;
        }

        /// <summary>
        /// 액션 수신: actions[0]=throttle(-1~1), actions[1]=steering(-1~1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _engine.RB == null || _episodeEnded)
                return;

            // LOS 가이던스 모드: 모델 추론 무시, 규칙 기반 추종
            if (useLOSGuidance)
            {
                ExecuteLOSGuidance();
                return;
            }

            float throttleInput = actions.ContinuousActions[0];
            float steeringInput = actions.ContinuousActions[1];

            // NaN 방지
            if (float.IsNaN(throttleInput) || float.IsInfinity(throttleInput)) throttleInput = 0f;
            if (float.IsNaN(steeringInput) || float.IsInfinity(steeringInput)) steeringInput = 0f;

            steeringInput = Mathf.Clamp(steeringInput, -1f, 1f);

            // Throttle Mapping: -1 → minThrottle, +1 → maxThrottle (기본 전진에서 감속 학습)
            float throttle = Mathf.Lerp(minThrottle, maxThrottle, (throttleInput + 1f) * 0.5f);

            // Steering 감도 적용
            float steering = Mathf.Clamp(steeringInput * steeringSensitivity, -1f, 1f);

            // 스무딩 (inputSmoothing < 1일 때만)
            if (inputSmoothing < 1f)
            {
                throttle = Mathf.Lerp(_prevThrottle, throttle, inputSmoothing);
                steering = Mathf.Lerp(_prevSteering, steering, inputSmoothing);
            }
            // 명령 변화량 기록 (보상 계산용)
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
        /// LOS 가이던스: 가장 가까운 적군을 향해 규칙 기반 조향
        /// 파트너와 좌우로 벌려서 그물 전개 유도
        /// </summary>
        private void ExecuteLOSGuidance()
        {
            // 1. 가장 가까운 활성 적군 찾기
            GameObject target = null;
            float nearestDist = float.MaxValue;
            Vector3 myPos = transform.position;

            if (enemyShips != null)
            {
                foreach (var enemy in enemyShips)
                {
                    if (enemy == null || !enemy.activeInHierarchy) continue;
                    if (enemy.transform.position.y < -100f) continue; // HIDDEN_POS
                    float d = Vector3.Distance(myPos, enemy.transform.position);
                    if (d < nearestDist) { nearestDist = d; target = enemy; }
                }
            }

            // 적군 없으면 직진
            if (target == null)
            {
                _engine.Accelerate(losThrottleMax);
                _engine.Turn(0f);
                _prevThrottle = losThrottleMax;
                _prevSteering = 0f;
                return;
            }

            // 2. 목표 지점 계산: 적군 위치에서 좌우 offset
            Vector3 enemyPos = target.transform.position;
            Vector3 toMother = motherShip != null
                ? (motherShip.transform.position - enemyPos)
                : -transform.forward;
            toMother.y = 0f;
            toMother.Normalize();

            // 적군→모선 방향의 수직 벡터 (왼쪽)
            Vector3 perpLeft = new Vector3(-toMother.z, 0f, toMother.x);

            // 적군 앞쪽(모선 방향)에 약간 선행, 좌우로 벌림
            float sign = isLeftAgent ? 1f : -1f;
            Vector3 targetPos = enemyPos + toMother * 20f + perpLeft * sign * losSpreadDistance;

            // 3. 비례 조향: 목표 방향과 현재 heading의 bearing 차이
            Vector3 toTarget = targetPos - myPos;
            toTarget.y = 0f;
            float targetAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            float myAngle = transform.eulerAngles.y;
            float bearing = Mathf.DeltaAngle(myAngle, targetAngle); // -180 ~ +180

            // steering = 비례 제어 (bearing/180 * gain), clamp to [-1, 1]
            float steering = Mathf.Clamp(bearing / 180f * losSteeringGain, -1f, 1f);

            // 4. 속도: 거리에 따라 조절 (멀면 최대, 가까우면 감속)
            float distToTarget = toTarget.magnitude;
            float throttle;
            if (distToTarget > losApproachDist)
                throttle = losThrottleMax;
            else
                throttle = Mathf.Lerp(losThrottleMin, losThrottleMax, distToTarget / losApproachDist);

            // 5. 엔진 구동
            _prevThrottle = throttle;
            _prevSteering = steering;
            _engine.Accelerate(throttle);
            _engine.Turn(steering);

            if (enableDebugLog)
            {
                Debug.Log($"[{name}] LOS: target={target.name}, dist={nearestDist:F0}m, bearing={bearing:F1}°, throttle={throttle:F2}, steer={steering:F2}");
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
        /// 수동 조종 (Agent1: WASD, Agent2: Arrow Keys)
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

            string agentName = gameObject.name.ToLower();
            bool isAgent1 = agentName.Contains("1") || agentName.Contains("agent1") || agentName.Contains("defense1");
            bool isAgent2 = agentName.Contains("2") || agentName.Contains("agent2") || agentName.Contains("defense2");

            if (isAgent1)
            {
                if (keyboard.wKey.isPressed) throttle = 1f;
                else if (keyboard.sKey.isPressed) throttle = -0.5f;
                if (keyboard.dKey.isPressed) steering = 1f;
                else if (keyboard.aKey.isPressed) steering = -1f;
            }
            else if (isAgent2)
            {
                if (keyboard.upArrowKey.isPressed) throttle = 1f;
                else if (keyboard.downArrowKey.isPressed) throttle = -0.5f;
                if (keyboard.rightArrowKey.isPressed) steering = 1f;
                else if (keyboard.leftArrowKey.isPressed) steering = -1f;
            }
            else
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) throttle = 1f;
                else if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) throttle = -0.5f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) steering = 1f;
                else if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) steering = -1f;
            }

            continuousActions[0] = Mathf.Clamp(throttle, -1f, 1f);
            continuousActions[1] = Mathf.Clamp(steering, -1f, 1f);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_episodeEnded)
                return;

            if (collision.gameObject.GetComponent<DefenseAgent>() != null ||
                collision.gameObject.CompareTag("MotherShip"))
            {
                // 멀티 환경 호환: 같은 환경 계층 내에서 컨트롤러 찾기
                Transform envRoot = transform.parent != null ? transform.parent : transform;
                DefenseEnvController envController = envRoot.GetComponentInChildren<DefenseEnvController>();
                if (envController != null)
                {
                    envController.OnFriendlyCollision();
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
