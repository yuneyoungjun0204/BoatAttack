using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;

namespace BoatAttack
{
    /// <summary>
    /// 방어 에이전트 협력 버전 (PushAgentCollab 패턴 기반)
    /// 연속 액션(Continuous Actions)을 사용하여 부드러운 이동/회전 제어
    /// - actions[0]: throttle (전진/후진: -1~1, -1=후진, 0=정지, 1=전진)
    /// - actions[1]: steering (좌회전/우회전: -1~1, -1=좌회전, 0=직진, 1=우회전)
    /// </summary>
    public class DefenseAgentCollab : Agent
    {
        [Header("Settings")]
        [Tooltip("방어 설정 (없으면 기본값 사용)")]
        private DefenseSettings m_DefenseSettings;
        
        [Header("Boat Components")]
        [Tooltip("Boat 컴포넌트 (자동으로 찾음)")]
        private Boat m_Boat;
        
        [Tooltip("Engine 컴포넌트 (자동으로 찾음)")]
        private Engine m_Engine;
        
        [Tooltip("Rigidbody (캐시)")]
        private Rigidbody m_AgentRb;
        
        [Header("Movement Settings")]
        [Tooltip("에이전트 이동 속도 (m/s, DefenseSettings가 없으면 이 값 사용)")]
        public float agentRunSpeed = 10f;
        
        [Tooltip("회전 속도 (deg/s, DefenseSettings가 없으면 이 값 사용)")]
        public float rotationSpeed = 200f;
        
        [Tooltip("조종 감도 (steering 입력에 적용되는 배율)")]
        [Range(0.1f, 2.0f)]
        public float steeringSensitivity = 1.0f;

        protected override void Awake()
        {
            base.Awake();
            
            // DefenseSettings 찾기 (없으면 기본값 사용)
            m_DefenseSettings = FindObjectOfType<DefenseSettings>();
            
            // Boat와 Engine 컴포넌트 찾기
            if (TryGetComponent(out m_Boat))
            {
                m_Engine = m_Boat.engine;
            }
            else
            {
                Debug.LogWarning($"[DefenseAgentCollab] Boat 컴포넌트를 찾을 수 없습니다. {gameObject.name}");
            }
        }

        public override void Initialize()
        {
            // Rigidbody 캐시 (PushAgentCollab 패턴)
            m_AgentRb = GetComponent<Rigidbody>();
            
            if (m_AgentRb == null)
            {
                Debug.LogError($"[DefenseAgentCollab] Rigidbody를 찾을 수 없습니다! {gameObject.name}");
            }
            
            // Engine이 있으면 Engine의 Rigidbody 사용
            if (m_Engine != null && m_Engine.RB != null)
            {
                m_AgentRb = m_Engine.RB;
            }
        }

        /// <summary>
        /// PushAgentCollab 패턴: 에이전트를 연속 액션에 따라 이동시킴
        /// </summary>
        /// <param name="throttle">전진/후진 (-1~1, -1=후진, 0=정지, 1=전진)</param>
        /// <param name="steering">좌회전/우회전 (-1~1, -1=좌회전, 0=직진, 1=우회전)</param>
        public void MoveAgent(float throttle, float steering)
        {
            if (m_AgentRb == null)
                return;
            
            // NaN 및 Infinity 방지
            if (float.IsNaN(throttle) || float.IsInfinity(throttle))
                throttle = 0f;
            if (float.IsNaN(steering) || float.IsInfinity(steering))
                steering = 0f;
            
            // 범위 제한
            throttle = Mathf.Clamp(throttle, -1f, 1f);
            steering = Mathf.Clamp(steering, -1f, 1f);
            
            // 실제 속도 값 결정 (DefenseSettings 우선, 없으면 인스펙터 값 사용)
            float actualSpeed = agentRunSpeed;
            float actualRotationSpeed = rotationSpeed;
            
            if (m_DefenseSettings != null)
            {
                actualSpeed = m_DefenseSettings.agentRunSpeed;
                actualRotationSpeed = m_DefenseSettings.agentRotationSpeed;
            }
            
            // Engine이 있으면 Engine을 통해 제어 (더 정확한 물리 시뮬레이션)
            if (m_Engine != null)
            {
                // 전진만 허용 (후진은 0으로 처리) 또는 후진도 허용하려면 주석 해제
                float throttleClamped = Mathf.Clamp01(throttle); // 0~1 범위 (전진만)
                // float throttleClamped = (throttle + 1f) * 0.5f; // 후진도 허용하려면 이 줄 사용
                
                // Steering에 감도 적용
                float steeringWithSensitivity = steering * steeringSensitivity;
                steeringWithSensitivity = Mathf.Clamp(steeringWithSensitivity, -1f, 1f);
                
                // Engine에 전달
                m_Engine.Accelerate(throttleClamped);
                m_Engine.Turn(steeringWithSensitivity);
            }
            else
            {
                // Engine이 없으면 Rigidbody에 직접 힘 적용 (PushAgentCollab 패턴)
                Vector3 moveDirection = transform.forward * throttle;
                
                // 회전 적용
                if (Mathf.Abs(steering) > 0.01f)
                {
                    transform.Rotate(transform.up, steering * actualRotationSpeed * Time.fixedDeltaTime);
                }
                
                // 이동 적용
                if (Mathf.Abs(throttle) > 0.01f)
                {
                    m_AgentRb.AddForce(moveDirection * actualSpeed, ForceMode.VelocityChange);
                }
            }
        }

        /// <summary>
        /// PushAgentCollab 패턴: 매 스텝마다 호출되어 에이전트가 액션을 취함
        /// </summary>
        public override void OnActionReceived(ActionBuffers actionBuffers)
        {
            // 연속 액션 값 가져오기
            float throttle = actionBuffers.ContinuousActions[0]; // 전진/후진: -1~1
            float steering = actionBuffers.ContinuousActions[1]; // 좌회전/우회전: -1~1
            
            // 에이전트를 액션에 따라 이동시킴
            MoveAgent(throttle, steering);
        }

        /// <summary>
        /// PushAgentCollab 패턴: 수동 조종 (키보드 테스트용)
        /// 연속 액션으로 변환: WASD를 throttle/steering으로 매핑
        /// 신형 Input System 사용 (UnityEngine.InputSystem)
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
            
            // 초기값: 정지
            float throttle = 0f;
            float steering = 0f;
            
            // 키보드 입력 감지 (신형 Input System 사용)
            if (keyboard.wKey.isPressed)
            {
                throttle = 1f; // 전진
            }
            else if (keyboard.sKey.isPressed)
            {
                throttle = -0.5f; // 후진 (느리게)
            }
            
            if (keyboard.aKey.isPressed)
            {
                steering = -1f; // 좌회전
            }
            else if (keyboard.dKey.isPressed)
            {
                steering = 1f; // 우회전
            }
            
            // 연속 액션 값 출력
            continuousActions[0] = Mathf.Clamp(throttle, -1f, 1f);
            continuousActions[1] = Mathf.Clamp(steering, -1f, 1f);
        }
    }
}
