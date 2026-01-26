using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections.Generic;
using System.Linq;

namespace BoatAttack
{
    /// <summary>
    /// 방어 에이전트: 2대가 협력하여 적군 선박을 web 사이로 유도하는 것이 목표
    /// 목표 속도 기반 액션 및 상대 좌표 기반 관측 사용
    /// 보상은 DefenseEnvController를 통해 그룹 보상으로 분배됨
    /// </summary>
    public class DefenseAgent : Agent
    {
        [Header("Boat Components")]
        [Tooltip("Boat 컴포넌트 (자동으로 찾음)")]
        private Boat _boat;

        [Tooltip("Engine 컴포넌트 (자동으로 찾음)")]
        public Engine _engine;

        [Header("Observation Settings")]
        [Tooltip("관측할 적군 선박들 (인스펙터에서 할당)")]
        public GameObject[] enemyShips = new GameObject[5];  // 최대 5대까지 지원

        [Tooltip("최대 관측 가능한 적군 수")]
        public int maxEnemyCount = 5;

        [Header("Target Settings")]
        [Tooltip("파트너 방어 선박 (다른 DefenseAgent)")]
        public DefenseAgent partnerAgent;

        [Tooltip("모선 (MotherShip 태그)")]
        public GameObject motherShip;

        [Tooltip("모선 태그")]
        public string motherShipTag = "MotherShip";

        [Tooltip("Web 오브젝트 (충돌 감지용, 2대 사이에 배치)")]
        public GameObject webObject;

        [Header("Action Settings")]
        [Tooltip("최대 선속도 (m/s)")]
        public float maxLinearVelocity = 20f;

        [Tooltip("최대 각속도 (deg/s)")]
        public float maxAngularVelocity = 90f;

        [Tooltip("속도 제어 게인 (PID 또는 비례 제어)")]
        public float velocityControlGain = 1.0f;

        [Tooltip("각속도 제어 게인")]
        public float angularVelocityControlGain = 1.0f;

        [Tooltip("최대 선가속도 (m/s²) - 물리적 한계")]
        public float maxLinearAcceleration = 5f;

        [Tooltip("최대 각가속도 (deg/s²) - 물리적 한계")]
        public float maxAngularAcceleration = 45f;

        [Header("Control Settings")]
        [Range(0.1f, 2.0f)]
        [Tooltip("조종 감도 조절")]
        public float steeringSensitivity = 0.3f;

        [Range(0.01f, 1.0f)]
        [Tooltip("입력 스무스 처리 속도")]
        public float inputSmoothing = 0.2f;

        [Header("Debug")]
        [Tooltip("에디터에서 Raycast 시각화")]
        public bool showRaycasts = true;

        [Tooltip("디버그 로그 활성화")]
        public bool enableDebugLog = false;

        [Header("Reward Display")]
        [Tooltip("현재 에피소드의 총 보상")]
        [SerializeField] private float _totalReward = 0f;

        [SerializeField] private float _lastStepReward = 0f;

        // 내부 변수
        private float _smoothThrottle = 0f;
        private float _smoothSteering = 0f;
        private bool _episodeEnded = false;
        
        // 목표 속도 추적 (가속도 제한용)
        private float _lastTargetLinearVelocity = 0f;
        private float _lastTargetAngularVelocity = 0f;

        private new void Awake()
        {
            // Boat와 Engine 컴포넌트 찾기
            if (TryGetComponent(out _boat))
            {
                _engine = _boat.engine;
            }
            else
            {
                Debug.LogError($"[DefenseAgent] Boat 컴포넌트를 찾을 수 없습니다. {gameObject.name}");
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            // 모선 찾기
            if (motherShip == null)
            {
                var ship = GameObject.FindGameObjectWithTag(motherShipTag);
                if (ship != null)
                {
                    motherShip = ship;
                }
                else
                {
                    Debug.LogWarning($"[DefenseAgent] 모선을 찾을 수 없습니다. 태그: {motherShipTag}");
                }
            }
        }

        public override void OnEpisodeBegin()
        {
            _episodeEnded = false;
            _totalReward = 0f;
            _lastStepReward = 0f;
            _smoothThrottle = 0f;
            _smoothSteering = 0f;
            _lastTargetLinearVelocity = 0f;
            _lastTargetAngularVelocity = 0f;

            if (enableDebugLog)
            {
                Debug.Log($"[DefenseAgent] 에피소드 시작: {gameObject.name}");
            }
        }

        /// <summary>
        /// 관측 수집 (상대 좌표 기반)
        /// - 자신: 위치(x,z), 헤딩, 속도 = 4개
        /// - 팀원: 상대 위치(x,z), 상대 헤딩, 속도 = 4개
        /// - 적군들: 상대 위치(x,z), 상대 헤딩, 속도 = 4 × 적군수 (거리순 정렬)
        /// - 모선: 상대 위치(x,z), 거리 = 3개
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            if (_engine == null || _engine.RB == null)
            {
                // 기본값 추가
                int totalObservations = 4 + 4 + (4 * maxEnemyCount) + 3;
                for (int i = 0; i < totalObservations; i++)
                {
                    sensor.AddObservation(0f);
                }
                return;
            }

            Vector3 myPos = transform.position;
            Vector3 myForward = transform.forward;
            Vector3 myRight = transform.right;
            float myAngle = transform.eulerAngles.y;

            // === 1. 자신의 상태 (4개) - 절대 좌표 유지 (기준점) ===
            sensor.AddObservation(myPos.x / 100f);
            sensor.AddObservation(myPos.z / 100f);
            sensor.AddObservation(myAngle / 360f);
            sensor.AddObservation(_engine.RB.velocity.magnitude / 20f);

            // === 2. 팀원(파트너) 상태 (4개) - 상대 좌표로 변경 ===
            if (partnerAgent != null && partnerAgent._engine != null && partnerAgent._engine.RB != null)
            {
                Vector3 relativeToPartner = partnerAgent.transform.position - myPos;
                // 상대 위치를 로컬 좌표계로 변환
                float relativeX = Vector3.Dot(relativeToPartner, myRight) / 100f;
                float relativeZ = Vector3.Dot(relativeToPartner, myForward) / 100f;
                float relativeDistance = relativeToPartner.magnitude / 100f;
                float relativeAngle = Mathf.DeltaAngle(myAngle, partnerAgent.transform.eulerAngles.y) / 180f;  // -1~1

                sensor.AddObservation(relativeX);
                sensor.AddObservation(relativeZ);
                sensor.AddObservation(relativeAngle);
                sensor.AddObservation(partnerAgent._engine.RB.velocity.magnitude / 20f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }

            // === 3. 적군 선박들 (4 × maxEnemyCount) - 거리 기반 정렬 + 상대 좌표 ===
            // 먼저 적군을 거리순으로 정렬
            List<(GameObject enemy, float distance)> sortedEnemies = new List<(GameObject, float)>();
            for (int i = 0; i < enemyShips.Length && i < maxEnemyCount; i++)
            {
                if (enemyShips[i] != null)
                {
                    float dist = Vector3.Distance(myPos, enemyShips[i].transform.position);
                    sortedEnemies.Add((enemyShips[i], dist));
                }
            }
            sortedEnemies.Sort((a, b) => a.distance.CompareTo(b.distance));  // 가까운 순으로 정렬

            // 정렬된 적군을 관측값에 추가
            for (int i = 0; i < maxEnemyCount; i++)
            {
                if (i < sortedEnemies.Count)
                {
                    GameObject enemy = sortedEnemies[i].enemy;
                    Vector3 relativeToEnemy = enemy.transform.position - myPos;

                    // 상대 좌표 (로컬 좌표계)
                    float relativeX = Vector3.Dot(relativeToEnemy, myRight) / 100f;
                    float relativeZ = Vector3.Dot(relativeToEnemy, myForward) / 100f;
                    float relativeDistance = relativeToEnemy.magnitude / 100f;
                    float relativeAngle = Mathf.DeltaAngle(myAngle, enemy.transform.eulerAngles.y) / 180f;

                    sensor.AddObservation(relativeX);
                    sensor.AddObservation(relativeZ);
                    sensor.AddObservation(relativeAngle);
                    
                    Rigidbody enemyRb = enemy.GetComponent<Rigidbody>();
                    sensor.AddObservation(enemyRb != null ? enemyRb.velocity.magnitude / 20f : 0f);
                }
                else
                {
                    // 0으로 채움
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                    sensor.AddObservation(0f);
                }
            }

            // === 4. 모선 상대거리 (3개) - 상대 좌표 ===
            if (motherShip != null)
            {
                Vector3 relativePos = motherShip.transform.position - myPos;
                float relativeX = Vector3.Dot(relativePos, myRight) / 1000f;
                float relativeZ = Vector3.Dot(relativePos, myForward) / 1000f;
                sensor.AddObservation(relativeX);
                sensor.AddObservation(relativeZ);
                sensor.AddObservation(relativePos.magnitude / 1000f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }
        }

        /// <summary>
        /// 액션 수신 및 실행 (목표 속도 기반)
        /// - actions[0]: 목표 선속도 (정규화: -1~1)
        /// - actions[1]: 목표 각속도 (정규화: -1~1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _episodeEnded)
                return;

            // 목표 속도 수신 (정규화된 값 -1~1)
            float targetLinearVelNormalized = actions.ContinuousActions[0];
            float targetAngularVelNormalized = actions.ContinuousActions[1];

            // 정규화 해제
            float targetLinearVelocity = targetLinearVelNormalized * maxLinearVelocity;
            float targetAngularVelocity = targetAngularVelNormalized * maxAngularVelocity;

            // 현재 속도 측정
            float currentLinearVelocity = Vector3.Dot(_engine.RB.velocity, transform.forward);
            float currentAngularVelocity = _engine.RB.angularVelocity.y * Mathf.Rad2Deg;

            // ⚠️ 가속도 제한 적용 (물리적 한계 고려)
            float deltaTime = Time.fixedDeltaTime;
            float maxLinearVelChange = maxLinearAcceleration * deltaTime;
            float maxAngularVelChange = maxAngularAcceleration * deltaTime;

            // 목표 속도 변화량 제한
            targetLinearVelocity = Mathf.Clamp(targetLinearVelocity,
                _lastTargetLinearVelocity - maxLinearVelChange,
                _lastTargetLinearVelocity + maxLinearVelChange);
            targetAngularVelocity = Mathf.Clamp(targetAngularVelocity,
                _lastTargetAngularVelocity - maxAngularVelChange,
                _lastTargetAngularVelocity + maxAngularVelChange);

            _lastTargetLinearVelocity = targetLinearVelocity;
            _lastTargetAngularVelocity = targetAngularVelocity;

            // 속도 차이 계산 및 throttle/steering 변환
            float velocityError = targetLinearVelocity - currentLinearVelocity;
            float throttle = Mathf.Clamp(velocityError * velocityControlGain / maxLinearVelocity, -1f, 1f);

            float angularVelocityError = targetAngularVelocity - currentAngularVelocity;
            float steering = Mathf.Clamp(angularVelocityError * angularVelocityControlGain / maxAngularVelocity, -1f, 1f);

            // ⚠️ 스무스 처리 강화 (더 보수적으로)
            float smoothFactor = 0.1f;  // 기존 inputSmoothing보다 더 작게 (더 부드럽게)
            _smoothThrottle = Mathf.Lerp(_smoothThrottle, throttle, smoothFactor);
            _smoothSteering = Mathf.Lerp(_smoothSteering, steering, smoothFactor);

            // Engine 제어
            _engine.Accelerate(Mathf.Clamp01(_smoothThrottle));  // throttle은 0~1 범위
            _engine.Turn(_smoothSteering * steeringSensitivity);

            // ⚠️ 개별 보상 제거 - DefenseEnvController에서 그룹 보상으로 처리
        }

        /// <summary>
        /// Web-적군 충돌 처리 (외부에서 호출)
        /// DefenseEnvController로 전달하여 그룹 보상으로 처리
        /// </summary>
        public void OnEnemyCaptured(Vector3 enemyPosition)
        {
            if (_episodeEnded)
                return;

            _episodeEnded = true;

            // DefenseEnvController에 알림 (그룹 보상으로 처리)
            DefenseEnvController envController = FindObjectOfType<DefenseEnvController>();
            if (envController != null)
            {
                envController.OnEnemyCaptured(enemyPosition);
            }

            if (enableDebugLog)
            {
                Debug.Log($"[DefenseAgent] 포획 성공! {gameObject.name}");
            }

            // 에피소드 종료는 DefenseEnvController에서 처리
        }

        /// <summary>
        /// 수동 조종 (키보드 테스트용) - 목표 속도 기반
        /// Agent1: WASD
        /// Agent2: Arrow Keys
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;

            // 키보드 입력을 목표 속도로 변환
            float targetLinearVel = 0f;
            float targetAngularVel = 0f;

            // Agent 이름으로 구분
            if (gameObject.name.Contains("1"))
            {
                // WASD
                if (Input.GetKey(KeyCode.W))
                    targetLinearVel = maxLinearVelocity;  // 전진
                else if (Input.GetKey(KeyCode.S))
                    targetLinearVel = -maxLinearVelocity * 0.5f;  // 후진 (느리게)

                if (Input.GetKey(KeyCode.D))
                    targetAngularVel = maxAngularVelocity;  // 우회전
                else if (Input.GetKey(KeyCode.A))
                    targetAngularVel = -maxAngularVelocity;  // 좌회전
            }
            else if (gameObject.name.Contains("2"))
            {
                // Arrow Keys
                if (Input.GetKey(KeyCode.UpArrow))
                    targetLinearVel = maxLinearVelocity;
                else if (Input.GetKey(KeyCode.DownArrow))
                    targetLinearVel = -maxLinearVelocity * 0.5f;

                if (Input.GetKey(KeyCode.RightArrow))
                    targetAngularVel = maxAngularVelocity;
                else if (Input.GetKey(KeyCode.LeftArrow))
                    targetAngularVel = -maxAngularVelocity;
            }

            // 정규화 (-1~1)
            continuousActions[0] = targetLinearVel / maxLinearVelocity;
            continuousActions[1] = targetAngularVel / maxAngularVelocity;
        }

        /// <summary>
        /// 아군 충돌 감지
        /// </summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (_episodeEnded)
                return;

            // 아군 또는 모선과 충돌 체크
            if (collision.gameObject.CompareTag("DefenseAgent") || 
                collision.gameObject.CompareTag("MotherShip"))
            {
                DefenseEnvController envController = FindObjectOfType<DefenseEnvController>();
                if (envController != null)
                {
                    envController.OnFriendlyCollision();
                }
            }
        }

        /// <summary>
        /// Gizmo 시각화
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
                return;

            // 자신 표시 (파란색)
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, 2f);

            // 파트너와 연결선 (녹색)
            if (partnerAgent != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, partnerAgent.transform.position);
            }

            // 적군과 연결선 (빨간색)
            if (enemyShips != null)
            {
                Gizmos.color = Color.red;
                foreach (var enemy in enemyShips)
                {
                    if (enemy != null)
                    {
                        Gizmos.DrawLine(transform.position, enemy.transform.position);
                    }
                }
            }

            // Web 위치 표시 (노란색)
            if (webObject != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(webObject.transform.position, 3f);
            }
        }
    }
}
