using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

namespace BoatAttack
{
    /// <summary>
    /// 방어 에이전트: 2대가 협력하여 적군 선박을 web 사이로 유도하는 것이 목표
    /// 보상: web과 attack_boat 충돌 시 큰 보상
    /// </summary>
    public class DefenseAgent : Agent
    {
        [Header("Boat Components")]
        [Tooltip("Boat 컴포넌트 (자동으로 찾음)")]
        private Boat _boat;

        [Tooltip("Engine 컴포넌트 (자동으로 찾음)")]
        private Engine _engine;

        [Header("Target Settings")]
        [Tooltip("공격 대상 적군 선박 (attack_boat 태그)")]
        public GameObject targetEnemy;

        [Tooltip("적군 선박 태그")]
        public string enemyTag = "attack_boat";

        [Tooltip("파트너 방어 선박 (다른 DefenseAgent)")]
        public DefenseAgent partnerAgent;

        [Tooltip("Web 오브젝트 (충돌 감지용, 2대 사이에 배치)")]
        public GameObject webObject;

        [Header("Observation Settings")]
        [Tooltip("Raycast로 주변 감지할 거리")]
        public float raycastDistance = 50f;

        [Tooltip("Raycast 방향 개수")]
        public int raycastCount = 8;

        [Header("Reward Settings")]
        [Tooltip("적군과 가까워질 때 보상 계수")]
        public float approachRewardMultiplier = 0.005f;

        [Tooltip("파트너와의 이상적인 거리 (web 기준)")]
        public float idealPartnerDistance = 40f;

        [Tooltip("파트너 거리 유지 보상 계수")]
        public float partnerDistanceReward = 0.01f;

        [Tooltip("적군을 web으로 유도하는 보상 계수")]
        public float webGuidanceReward = 0.02f;

        [Tooltip("시간당 작은 페널티 (빠른 포획 유도)")]
        public float timePenalty = -0.001f;

        [Tooltip("Web-적군 충돌 시 큰 보상")]
        public float captureReward = 10.0f;

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
        private float _lastEnemyDistance;
        private float _smoothThrottle = 0f;
        private float _smoothSteering = 0f;
        private bool _episodeEnded = false;

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

            // 적군 찾기
            if (targetEnemy == null)
            {
                var enemy = GameObject.FindGameObjectWithTag(enemyTag);
                if (enemy != null)
                {
                    targetEnemy = enemy;
                }
                else
                {
                    Debug.LogWarning($"[DefenseAgent] 적군을 찾을 수 없습니다. 태그: {enemyTag}");
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

            // 적군과의 초기 거리 저장
            if (targetEnemy != null)
            {
                _lastEnemyDistance = Vector3.Distance(transform.position, targetEnemy.transform.position);
            }

            if (enableDebugLog)
            {
                Debug.Log($"[DefenseAgent] 에피소드 시작: {gameObject.name}");
            }
        }

        /// <summary>
        /// 관측 수집 (총 20개)
        /// - 자신: 위치(x,z), 각도(y), 속도 = 4개
        /// - 파트너: 상대 위치(x,z), 각도(y), 속도 = 4개
        /// - 적군: 상대 위치(x,z), 각도(y), 속도 = 4개
        /// - Raycast 센서: 8개
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            if (_engine == null || _engine.RB == null)
            {
                // 기본값 추가 (20개)
                for (int i = 0; i < 20; i++)
                {
                    sensor.AddObservation(0f);
                }
                return;
            }

            // === 1. 자신의 상태 (4개) ===
            Vector3 myPos = transform.position;
            float myAngle = transform.eulerAngles.y;
            float mySpeed = _engine.RB.velocity.magnitude;

            // 위치 (정규화: /100)
            sensor.AddObservation(myPos.x / 100f);
            sensor.AddObservation(myPos.z / 100f);

            // 각도 (정규화: 0~360 → 0~1)
            sensor.AddObservation(myAngle / 360f);

            // 속도 (정규화: /20)
            sensor.AddObservation(mySpeed / 20f);

            // === 2. 파트너의 상태 (4개) ===
            if (partnerAgent != null && partnerAgent._engine != null && partnerAgent._engine.RB != null)
            {
                Vector3 partnerPos = partnerAgent.transform.position;
                Vector3 relativePartnerPos = partnerPos - myPos;
                float partnerAngle = partnerAgent.transform.eulerAngles.y;
                float partnerSpeed = partnerAgent._engine.RB.velocity.magnitude;

                // 상대 위치
                sensor.AddObservation(relativePartnerPos.x / 100f);
                sensor.AddObservation(relativePartnerPos.z / 100f);

                // 각도
                sensor.AddObservation(partnerAngle / 360f);

                // 속도
                sensor.AddObservation(partnerSpeed / 20f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }

            // === 3. 적군의 상태 (4개) ===
            if (targetEnemy != null)
            {
                Vector3 enemyPos = targetEnemy.transform.position;
                Vector3 relativeEnemyPos = enemyPos - myPos;
                float enemyAngle = targetEnemy.transform.eulerAngles.y;

                // 적군 속도 (Rigidbody 확인)
                float enemySpeed = 0f;
                Rigidbody enemyRb = targetEnemy.GetComponent<Rigidbody>();
                if (enemyRb != null)
                {
                    enemySpeed = enemyRb.velocity.magnitude;
                }

                // 상대 위치
                sensor.AddObservation(relativeEnemyPos.x / 100f);
                sensor.AddObservation(relativeEnemyPos.z / 100f);

                // 각도
                sensor.AddObservation(enemyAngle / 360f);

                // 속도
                sensor.AddObservation(enemySpeed / 20f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }

            // === 4. Raycast 센서 (8개) ===
            for (int i = 0; i < raycastCount; i++)
            {
                float angle = (360f / raycastCount) * i;
                Vector3 rayDirection = Quaternion.Euler(0, angle, 0) * transform.forward;

                if (Physics.Raycast(transform.position, rayDirection, out RaycastHit hit, raycastDistance))
                {
                    // 거리 정규화 (0~1, 가까울수록 1)
                    float normalizedDistance = 1f - (hit.distance / raycastDistance);
                    sensor.AddObservation(normalizedDistance);

                    if (showRaycasts)
                    {
                        Debug.DrawRay(transform.position, rayDirection * hit.distance, Color.red);
                    }
                }
                else
                {
                    sensor.AddObservation(0f);

                    if (showRaycasts)
                    {
                        Debug.DrawRay(transform.position, rayDirection * raycastDistance, Color.green);
                    }
                }
            }
        }

        /// <summary>
        /// 액션 수신 및 실행
        /// - actions[0]: throttle (-1~1)
        /// - actions[1]: steering (-1~1)
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_engine == null || _episodeEnded)
                return;

            // 연속 액션 받기
            float throttle = actions.ContinuousActions[0];
            float steering = actions.ContinuousActions[1];

            // 스무스 처리
            _smoothThrottle = Mathf.Lerp(_smoothThrottle, throttle, inputSmoothing);
            _smoothSteering = Mathf.Lerp(_smoothSteering, steering, inputSmoothing);

            // Engine 제어
            _engine.Accelerate(_smoothThrottle);
            _engine.Turn(_smoothSteering * steeringSensitivity);

            // 보상 계산
            CalculateReward();
        }

        /// <summary>
        /// 보상 함수
        /// </summary>
        private void CalculateReward()
        {
            float reward = 0f;

            if (targetEnemy == null || _engine == null)
            {
                AddReward(timePenalty);
                return;
            }

            Vector3 myPos = transform.position;
            Vector3 enemyPos = targetEnemy.transform.position;
            float enemyDistance = Vector3.Distance(myPos, enemyPos);

            // === 1. 적군 접근 보상 ===
            float distanceImprovement = _lastEnemyDistance - enemyDistance;
            if (distanceImprovement > 0)
            {
                reward += distanceImprovement * approachRewardMultiplier;
            }
            _lastEnemyDistance = enemyDistance;

            // === 2. 파트너와 거리 유지 보상 ===
            if (partnerAgent != null)
            {
                float partnerDistance = Vector3.Distance(myPos, partnerAgent.transform.position);
                float distanceError = Mathf.Abs(partnerDistance - idealPartnerDistance);

                // 이상적인 거리에 가까울수록 보상
                if (distanceError < 20f)
                {
                    reward += (20f - distanceError) / 20f * partnerDistanceReward;
                }
            }

            // === 3. 적군을 web 방향으로 유도 보상 ===
            if (webObject != null)
            {
                Vector3 webPos = webObject.transform.position;
                float enemyToWebDistance = Vector3.Distance(enemyPos, webPos);

                // 적군이 web에 가까울수록 보상
                if (enemyToWebDistance < 30f)
                {
                    reward += (30f - enemyToWebDistance) / 30f * webGuidanceReward;
                }
            }

            // === 4. 시간 페널티 ===
            reward += timePenalty;

            // 보상 적용
            AddReward(reward);
            _totalReward += reward;
            _lastStepReward = reward;
        }

        /// <summary>
        /// Web-적군 충돌 처리 (외부에서 호출)
        /// </summary>
        public void OnEnemyCaptured()
        {
            if (_episodeEnded)
                return;

            _episodeEnded = true;

            // 큰 보상 제공
            AddReward(captureReward);
            _totalReward += captureReward;

            if (enableDebugLog)
            {
                Debug.Log($"[DefenseAgent] 포획 성공! 보상: {captureReward}, 총 보상: {_totalReward}");
            }

            // 에피소드 종료
            EndEpisode();
        }

        /// <summary>
        /// 수동 조종 (키보드 테스트용)
        /// Agent1: WASD
        /// Agent2: Arrow Keys
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var continuousActions = actionsOut.ContinuousActions;

            // Agent 이름으로 구분
            if (gameObject.name.Contains("1"))
            {
                // WASD
                continuousActions[0] = Input.GetKey(KeyCode.W) ? 1f : Input.GetKey(KeyCode.S) ? -1f : 0f;
                continuousActions[1] = Input.GetKey(KeyCode.D) ? 1f : Input.GetKey(KeyCode.A) ? -1f : 0f;
            }
            else if (gameObject.name.Contains("2"))
            {
                // Arrow Keys
                continuousActions[0] = Input.GetKey(KeyCode.UpArrow) ? 1f : Input.GetKey(KeyCode.DownArrow) ? -1f : 0f;
                continuousActions[1] = Input.GetKey(KeyCode.RightArrow) ? 1f : Input.GetKey(KeyCode.LeftArrow) ? -1f : 0f;
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
            if (targetEnemy != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position, targetEnemy.transform.position);
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
