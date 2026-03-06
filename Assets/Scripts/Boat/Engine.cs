using System;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using WaterSystem;

namespace BoatAttack
{
    public class Engine : MonoBehaviour
    {
        [NonSerialized] public Rigidbody RB; // The rigid body attatched to the boat
        [NonSerialized] public float VelocityMag; // Boats velocity

        public AudioSource engineSound; // Engine sound clip
        public AudioSource waterSound; // Water sound clip

        //engine stats
        public float steeringTorque = 5f;
        public float horsePower = 1500f;

        [Header("Speed Limit")]
        [Tooltip("Max speed in m/s (0 = no limit)")]
        public float maxSpeed = 1000f;

        [Header("Stabilization")]
        [Tooltip("자세 안정화 토크 강도 (0이면 비활성)")]
        public float stabilizationTorque = 2f;
        [Tooltip("안정화 감쇠력 (흔들림 방지)")]
        public float stabilizationDamping = 0.8f;
        [Tooltip("안정화 데드존 (도) - 이 각도 미만은 파도 흔들림 허용")]
        public float stabilizationDeadZone = 10f;
        [Tooltip("최대 허용 기울기 (도) - 이 각도 초과 시 강제 복원")]
        [Range(10f, 80f)]
        public float maxTiltAngle = 45f;
        [Tooltip("무게중심 오프셋 (로컬 좌표, Y를 낮추면 안정적)")]
        public Vector3 centerOfMassOffset = new Vector3(0f, -1f, 0f);

        [Header("Wind")]
        [Tooltip("바람이 선박에 미치는 힘 계수")]
        public float windForceMultiplier = 8000f;

        [Header("Environment Response")]
        [Tooltip("파도/바람 민감도 (-1=자동감지, 1.0=아군 강한 영향, 0.2=적군 약한 영향)")]
        public float environmentSensitivity = -1f;
        private float _envSens = 1f; // 런타임 민감도
        private NativeArray<float3> _point; // engine submerged check
        private float3[] _heights = new float3[1]; // engine submerged check
        private float3[] _normals = new float3[1]; // engine submerged check
        private int _guid;
        private float _yHeight;

        public Vector3 enginePosition;
        private Vector3 _engineDir;
        private float _turnVel;
        private float _currentAngle;

        // 에피소드 시작 시 _yHeight 조건 무시용 카운터 (향후 사용 예정)
        #pragma warning disable CS0414
        private int _skipHeightCheckFrames = 0;
        #pragma warning restore CS0414
        private const int SKIP_FRAMES_ON_RESET = 50; // 50프레임 동안 waterFactor=1
        private int _stabilizeFrames = 0;
        private const int STABILIZE_FRAMES_ON_RESET = 50; // 50프레임 동안 y축 속도 억제
        private bool _windDebugLogged = false;

        private void Awake()
        {
			if(engineSound)
				engineSound.time = UnityEngine.Random.Range(0f, engineSound.clip.length); // randomly start the engine sound

			if(waterSound)
				waterSound.time = UnityEngine.Random.Range(0f, waterSound.clip.length); // randomly start the water sound

            _guid = GetInstanceID(); // Get the engines GUID for the buoyancy system
            _point = new NativeArray<float3>(1, Allocator.Persistent);

            // 무게중심 설정 (RB는 Boat.Awake에서 할당되므로 1프레임 지연 적용)
            Invoke(nameof(ApplyCenterOfMass), 0f);
        }

        private void ApplyCenterOfMass()
        {
            if (RB != null)
                RB.centerOfMass = centerOfMassOffset;
        }

        private void FixedUpdate()
        {
            // ⚠️ RB null 체크
            if (RB == null)
            {
                return;
            }
            
            // ⚠️ NaN 방지: Rigidbody rotation 체크 및 리셋
            if (RB != null)
            {
                Quaternion rbRotation = RB.rotation;
                if (float.IsNaN(rbRotation.x) || float.IsNaN(rbRotation.y) || 
                    float.IsNaN(rbRotation.z) || float.IsNaN(rbRotation.w))
                {
                    Debug.LogError($"[Engine] ⚠️ Rigidbody rotation이 NaN입니다! 리셋합니다. {gameObject.name}");
                    RB.rotation = Quaternion.identity;
                    RB.angularVelocity = Vector3.zero;
                    transform.rotation = Quaternion.identity;
                    _currentAngle = 0f;
                    _turnVel = 0f;
                }
                
                // ⚠️ NaN 방지: Rigidbody position 체크
                Vector3 rbPosition = RB.position;
                if (float.IsNaN(rbPosition.x) || float.IsNaN(rbPosition.y) || float.IsNaN(rbPosition.z))
                {
                    Debug.LogError($"[Engine] ⚠️ Rigidbody position이 NaN입니다! 리셋합니다. {gameObject.name}");
                    RB.position = transform.position;
                    RB.velocity = Vector3.zero;
                }
                
                // ⚠️ NaN 방지: Rigidbody velocity 체크
                Vector3 rbVelocity = RB.velocity;
                if (float.IsNaN(rbVelocity.x) || float.IsNaN(rbVelocity.y) || float.IsNaN(rbVelocity.z))
                {
                    Debug.LogWarning($"[Engine] ⚠️ Rigidbody velocity가 NaN입니다! 리셋합니다. {gameObject.name}");
                    RB.velocity = Vector3.zero;
                }
            }
            
            // 리셋 직후 안정화: y축 속도 제거 + 회전 제거 + 자세 강제 복원
            if (_stabilizeFrames > 0)
            {
                _stabilizeFrames--;
                // y축 속도 완전 제거
                Vector3 v = RB.velocity;
                v.y = 0f;
                RB.velocity = v;
                // 회전 속도 완전 제거
                RB.angularVelocity = Vector3.zero;
                // pitch/roll 강제 0으로 (yaw만 유지)
                Vector3 euler = RB.rotation.eulerAngles;
                RB.MoveRotation(Quaternion.Euler(0f, euler.y, 0f));
            }

            // 자세 안정화: 민감도에 따라 강도 조절 (아군=약, 적군=강)
            if (stabilizationTorque > 0f)
            {
                float timeScaleMul = Mathf.Max(1f, Mathf.Sqrt(Time.timeScale));
                float stabScale = 1f / Mathf.Max(0.1f, _envSens); // 아군(1.0)=1x, 적군(0.2)=5x
                Vector3 correctionAxis = Vector3.Cross(RB.transform.up, Vector3.up);
                float tiltAngle = Vector3.Angle(RB.transform.up, Vector3.up);
                float effectiveDeadZone = stabilizationDeadZone * _envSens; // 아군=10°, 적군=2°

                // 소프트 데드존: 데드존 내에서도 약간의 안정화
                float urgency;
                if (tiltAngle > maxTiltAngle)
                    urgency = 1f + (tiltAngle - maxTiltAngle) / 15f;
                else if (tiltAngle > effectiveDeadZone)
                    urgency = (tiltAngle - effectiveDeadZone) / Mathf.Max(1f, maxTiltAngle - effectiveDeadZone);
                else
                    urgency = (tiltAngle / Mathf.Max(1f, effectiveDeadZone)) * 0.15f;

                float torque = stabilizationTorque * urgency * timeScaleMul * stabScale;
                float damping = stabilizationDamping * Mathf.Max(urgency, 0.1f) * timeScaleMul * stabScale;
                RB.AddTorque(correctionAxis * torque - RB.angularVelocity * damping, ForceMode.Acceleration);

                // 극단적 기울기(거의 뒤집힘) 시 회전 직접 보정
                if (tiltAngle > maxTiltAngle + 20f)
                {
                    Vector3 euler = RB.rotation.eulerAngles;
                    float roll = euler.z > 180f ? euler.z - 360f : euler.z;
                    float pitch = euler.x > 180f ? euler.x - 360f : euler.x;
                    roll = Mathf.Clamp(roll, -maxTiltAngle, maxTiltAngle);
                    pitch = Mathf.Clamp(pitch, -maxTiltAngle, maxTiltAngle);
                    RB.MoveRotation(Quaternion.Euler(pitch, euler.y, roll));
                    RB.angularVelocity = Vector3.Scale(RB.angularVelocity, new Vector3(0.3f, 1f, 0.3f));
                }
            }

            // 파도 저항: 기울기에 비례하여 속도 감쇄 (민감도에 비례)
            {
                float tiltAngle = Vector3.Angle(RB.transform.up, Vector3.up);
                if (tiltAngle > 2f)
                {
                    float waveDragFactor = Mathf.Clamp01(tiltAngle / 30f) * 0.02f * _envSens;
                    RB.velocity *= 1f - waveDragFactor;
                }
            }

            // 바람 물리: 민감도에 비례하여 풍력 적용
            if (windForceMultiplier > 0f)
            {
                if (!WindzoneExtended.Initialized)
                    WindzoneExtended.EnsureInitialized();

                float windSpeed = WindzoneExtended.WindSpeed;
                if (windSpeed > 0f)
                {
                    Vector3 windDir = WindzoneExtended.WindDirection;
                    Vector3 windForce = windDir * windSpeed * windForceMultiplier * _envSens;
                    RB.AddForce(windForce, ForceMode.Force);

                    // 디버그: 첫 프레임에만 풍력 정보 출력
                    if (!_windDebugLogged)
                    {
                        _windDebugLogged = true;
                        float forceMag = windForce.magnitude;
                        float accel = RB.mass > 0f ? forceMag / RB.mass : 0f;
                        Debug.Log($"[Wind] {gameObject.name}: force={forceMag:F1}N, accel={accel:F3}m/s², " +
                            $"multiplier={windForceMultiplier}, windSpeed={windSpeed:F2}, sens={_envSens}, " +
                            $"mass={RB.mass}, kinematic={RB.isKinematic}");
                    }
                }
            }

            // Speed limiter: clamp velocity to maxSpeed
            if (maxSpeed > 0f)
            {
                Vector3 vel = RB.velocity;
                float speed = vel.magnitude;
                if (speed > maxSpeed)
                {
                    RB.velocity = vel * (maxSpeed / speed);
                }
            }

            VelocityMag = RB != null ? RB.velocity.sqrMagnitude : 0f; // get the sqr mag
            if (engineSound != null)
            {
                engineSound.pitch = Mathf.Max(VelocityMag * 0.01f, 0.3f); // use some magice numbers to control the pitch of the engine sound
            }

            // Get the water level from the engines position and store it
            // ⚠️ NativeArray가 생성되어 있는지 확인 (메모리 에러 방지)
            if (!_point.IsCreated)
            {
                return;
            }
            
            _point[0] = transform.TransformPoint(enginePosition);
            GerstnerWavesJobs.UpdateSamplePoints(ref _point, _guid);
            GerstnerWavesJobs.GetData(_guid, ref _heights, ref _normals);
            _yHeight = _heights[0].y - _point[0].y;
            
            // ⚠️ NaN 방지: _yHeight 체크
            if (float.IsNaN(_yHeight) || float.IsInfinity(_yHeight))
            {
                _yHeight = 0f;
            }
        }

        private void OnEnable()
        {
            // 비활성→활성 전환 시 _point 재생성 (OnDisable에서 Dispose됨)
            if (!_point.IsCreated)
            {
                _point = new NativeArray<float3>(1, Allocator.Persistent);
            }
            // RB 재확인
            if (RB == null)
            {
                RB = GetComponentInParent<Rigidbody>();
            }
            // 직렬화 값 마이그레이션: 이전 기본값이 직렬화된 프리팹/인스턴스 보정
            if (windForceMultiplier <= 2000f)
                windForceMultiplier = 3500f;
            // 환경 민감도 자동 감지
            InitEnvironmentSensitivity();
            // WindzoneExtended가 씬에 없어도 바람 데이터 초기화
            WindzoneExtended.EnsureInitialized();
            // 디버그 로그 리셋 (에피소드마다 한 번 출력)
            _windDebugLogged = false;
        }

        private void InitEnvironmentSensitivity()
        {
            if (environmentSensitivity >= 0f)
            {
                _envSens = environmentSensitivity;
                return;
            }
            // 자동 감지: DefenseAgent가 있으면 아군(1.0), 없으면 적군(0.2)
            foreach (var comp in GetComponentsInParent<MonoBehaviour>(true))
            {
                if (comp.GetType().Name == "DefenseAgent")
                {
                    _envSens = 1.0f;
                    return;
                }
            }
            _envSens = 0.2f;
        }

        private void OnDisable()
        {
            // ⚠️ NativeArray가 생성되어 있는지 확인 후 Dispose (중복 해제 방지)
            if (_point.IsCreated)
            {
                _point.Dispose();
            }
        }
        
        private void OnDestroy()
        {
            // ⚠️ OnDestroy에서도 안전하게 Dispose (이중 안전장치)
            if (_point.IsCreated)
            {
                _point.Dispose();
            }
        }

        /// <summary>
        /// 에피소드 리셋 시 호출 - 몇 프레임 동안 waterFactor=1 + y축 속도 억제
        /// </summary>
        public void OnEpisodeReset()
        {
            _skipHeightCheckFrames = SKIP_FRAMES_ON_RESET;
            _stabilizeFrames = STABILIZE_FRAMES_ON_RESET;
            _yHeight = 0f;  // 수면 위로 가정
        }

        /// <summary>
        /// Controls the acceleration of the boat
        /// </summary>
        /// <param name="modifier">Acceleration modifier, adds force in the 0-1 range</param>
        public void Accelerate(float modifier)
        {
            // ⚠️ NaN 방지: modifier 값 검증
            if (float.IsNaN(modifier) || float.IsInfinity(modifier))
            {
                modifier = 0f;
            }
            
            modifier = Mathf.Clamp(modifier, 0f, 1f); // clamp for reasonable values

            // _yHeight 기반 수면 감쇄: 엔진이 수면 위로 크게 나오면 추진력 감소
            // _yHeight ≥ -0.5: 100% 추진 (정상 파도 범위), _yHeight ≤ -1.5: 0% 추진 (공중)
            // 리셋 직후 몇 프레임은 waterFactor=1 (Gerstner 파도 안정화 대기)
            float waterFactor;
            if (_skipHeightCheckFrames > 0)
            {
                waterFactor = 1f;
                _skipHeightCheckFrames--;
            }
            else
            {
                waterFactor = Mathf.Clamp01((_yHeight + 1.5f) / 1.0f);
            }
            if (RB != null)
            {
                var forward = RB.transform.forward;
                forward.y = 0f;
                forward.Normalize();

                // ⚠️ NaN 방지: 벡터 검증
                if (float.IsNaN(forward.x) || float.IsNaN(forward.y) || float.IsNaN(forward.z))
                {
                    forward = Vector3.forward;
                }

                RB.AddForce(horsePower * modifier * waterFactor * forward, ForceMode.Acceleration);
                RB.AddRelativeTorque(-Vector3.right * modifier * waterFactor, ForceMode.Acceleration);
            }
        }

        /// <summary>
        /// Controls the turning of the boat
        /// </summary>
        /// <param name="modifier">Steering modifier, positive for right, negative for negative</param>
        public void Turn(float modifier)
        {
            // ⚠️ NaN 방지: modifier 값 검증
            if (float.IsNaN(modifier) || float.IsInfinity(modifier))
            {
                modifier = 0f;
            }
            
            modifier = Mathf.Clamp(modifier, -1f, 1f); // clamp for reasonable values

            // _yHeight 기반 수면 감쇄 (리셋 직후는 Accelerate에서 카운트다운)
            float turnWaterFactor = (_skipHeightCheckFrames > 0) ? 1f : Mathf.Clamp01((_yHeight + 1.5f) / 1.0f);
            if (RB != null)
            {
                // ⚠️ NaN 방지: torque 벡터 검증
                // Z축 Roll 제거: 선회 시 기울어지지 않아 직진 성능 유지
                Vector3 torque = new Vector3(0f, steeringTorque, 0f) * modifier * turnWaterFactor;
                if (float.IsNaN(torque.x) || float.IsNaN(torque.y) || float.IsNaN(torque.z))
                {
                    torque = Vector3.zero;
                }
                RB.AddRelativeTorque(torque, ForceMode.Acceleration); // add torque based on input and torque amount
            }

            // ⚠️ NaN 방지: _currentAngle과 _turnVel 검증
            if (float.IsNaN(_currentAngle) || float.IsInfinity(_currentAngle))
            {
                _currentAngle = 0f;
            }
            if (float.IsNaN(_turnVel) || float.IsInfinity(_turnVel))
            {
                _turnVel = 0f;
            }
            
            // ⚠️ NaN 방지: Time.fixedDeltaTime 사용 (Time.fixedTime 대신)
            float deltaTime = Time.fixedDeltaTime;
            if (deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
            {
                deltaTime = 0.02f; // 기본값
            }
            
            float targetAngle = 60f * -modifier;
            if (float.IsNaN(targetAngle) || float.IsInfinity(targetAngle))
            {
                targetAngle = 0f;
            }
            
            _currentAngle = Mathf.SmoothDampAngle(_currentAngle, 
                targetAngle, 
                ref _turnVel, 
                0.5f, 
                10f,
                deltaTime);
            
            // ⚠️ 최종 NaN 체크
            if (float.IsNaN(_currentAngle) || float.IsInfinity(_currentAngle))
            {
                _currentAngle = 0f;
            }
            
            // ⚠️ 각도 범위 제한
            _currentAngle = Mathf.Clamp(_currentAngle, -180f, 180f);
            
            // ⚠️ 최종 NaN 체크 후 transform 설정
            if (float.IsNaN(_currentAngle) || float.IsInfinity(_currentAngle))
            {
                _currentAngle = 0f;
            }
            
            Vector3 eulerAngles = new Vector3(0f, _currentAngle, 0f);
            
            // ⚠️ NaN 방지: eulerAngles 검증
            if (float.IsNaN(eulerAngles.x) || float.IsNaN(eulerAngles.y) || float.IsNaN(eulerAngles.z))
            {
                eulerAngles = Vector3.zero;
                _currentAngle = 0f;
            }
            
            transform.localEulerAngles = eulerAngles;
            
            // ⚠️ NaN 방지: transform.rotation도 체크
            Quaternion currentRotation = transform.rotation;
            if (float.IsNaN(currentRotation.x) || float.IsNaN(currentRotation.y) || 
                float.IsNaN(currentRotation.z) || float.IsNaN(currentRotation.w))
            {
                Debug.LogError($"[Engine] ⚠️ transform.rotation이 NaN입니다! 리셋합니다. {gameObject.name}");
                transform.rotation = Quaternion.identity;
                _currentAngle = 0f;
                if (RB != null)
                {
                    RB.rotation = Quaternion.identity;
                    RB.angularVelocity = Vector3.zero;
                }
            }
        }

        // Draw some helper gizmos
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(enginePosition, new Vector3(0.1f, 0.2f, 0.3f)); // Draw teh engine position with sphere
        }
	}
}
