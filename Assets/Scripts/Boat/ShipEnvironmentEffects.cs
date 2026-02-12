using UnityEngine;

/// <summary>
/// 선박에 환경 영향 적용 (바람, 파도)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ShipEnvironmentEffects : MonoBehaviour
{
    [Header("=== 환경 컨트롤러 ===")]
    [Tooltip("Environment Controller (자동 탐색 또는 수동 할당)")]
    public EnvironmentController environmentController;
    
    [Header("=== 바람 영향 ===")]
    [Tooltip("바람 영향 받음")]
    public bool applyWind = true;
    
    [Tooltip("바람 영향 계수 (0 = 영향 없음, 1 = 완전히 영향 받음)")]
    [Range(0f, 2f)]
    public float windInfluence = 0.3f;
    
    [Tooltip("선박 측면 면적 (m²) - 바람 저항 계산용")]
    [Range(1f, 100f)]
    public float lateralArea = 10f;
    
    [Header("=== 파도 영향 ===")]
    [Tooltip("파도 영향 받음")]
    public bool applyWaves = true;
    
    [Tooltip("파도 영향 계수")]
    [Range(0f, 2f)]
    public float waveInfluence = 1.0f;
    
    [Tooltip("파도에 의한 롤링/피칭")]
    public bool applyWaveMotion = true;
    
    [Tooltip("롤링 강도")]
    [Range(0f, 5f)]
    public float rollStrength = 1.0f;
    
    [Tooltip("피칭 강도")]
    [Range(0f, 5f)]
    public float pitchStrength = 0.5f;
    
    [Header("=== 디버그 ===")]
    public bool showDebugInfo = false;
    
    // 내부 변수
    private Rigidbody rb;
    private float baseWaterHeight = 0f;
    
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        
        // Environment Controller 자동 탐색
        if (environmentController == null)
        {
            environmentController = FindObjectOfType<EnvironmentController>();
            
            if (environmentController == null)
            {
                Debug.LogWarning($"[{gameObject.name}] EnvironmentController not found!");
            }
        }
    }
    
    void FixedUpdate()
    {
        if (environmentController == null) return;
        
        if (applyWind)
        {
            ApplyWindForce();
        }
        
        if (applyWaves)
        {
            ApplyWaveForce();
        }
        
        if (applyWaveMotion)
        {
            ApplyWaveMotion();
        }
    }
    
    /// <summary>
    /// 바람 힘 적용
    /// </summary>
    void ApplyWindForce()
    {
        Vector3 windVector = environmentController.GetWindVector();
        
        // 바람 저항 계산 (간이 공식)
        // F = 0.5 * ρ * A * Cd * v²
        // ρ = 공기 밀도 (1.225 kg/m³)
        // A = 측면 면적
        // Cd = 항력 계수 (≈ 1.0)
        // v = 바람 속도
        
        float airDensity = 1.225f;
        float dragCoefficient = 1.0f;
        float windSpeed = windVector.magnitude;
        
        Vector3 windForce = windVector.normalized * 
                           (0.5f * airDensity * lateralArea * dragCoefficient * windSpeed * windSpeed);
        
        // 영향 계수 적용
        windForce *= windInfluence;
        
        // 힘 적용 (선박 중심 위쪽에 적용 → 회전 모멘트 발생)
        Vector3 forcePoint = transform.position + Vector3.up * 2f;
        rb.AddForceAtPosition(windForce, forcePoint, ForceMode.Force);
        
        if (showDebugInfo)
        {
            Debug.Log($"[{gameObject.name}] Wind Force: {windForce.magnitude:F2}N, Direction: {windVector.normalized}");
        }
    }
    
    /// <summary>
    /// 파도 힘 적용 (부력 변화)
    /// </summary>
    void ApplyWaveForce()
    {
        // 현재 위치의 파도 높이 가져오기
        float waveHeight = environmentController.GetWaveHeightAt(transform.position);
        
        // 파도에 의한 수직 힘
        float waveForce = waveHeight * waveInfluence * 100f;  // 스케일 조정
        rb.AddForce(Vector3.up * waveForce, ForceMode.Force);
        
        if (showDebugInfo && Time.frameCount % 60 == 0)
        {
            Debug.Log($"[{gameObject.name}] Wave Height: {waveHeight:F2}m, Force: {waveForce:F2}N");
        }
    }
    
    /// <summary>
    /// 파도에 의한 롤링/피칭
    /// </summary>
    void ApplyWaveMotion()
    {
        // 파도 높이 기반 롤링/피칭
        float waveHeight = environmentController.GetWaveHeightAt(transform.position);
        float time = Time.time;
        
        // 롤링 (좌우 흔들림)
        float rollAngle = Mathf.Sin(time * 0.5f + transform.position.x * 0.1f) * rollStrength * waveHeight;
        
        // 피칭 (앞뒤 흔들림)
        float pitchAngle = Mathf.Sin(time * 0.7f + transform.position.z * 0.1f) * pitchStrength * waveHeight;
        
        // 토크 적용
        rb.AddTorque(transform.forward * rollAngle, ForceMode.Force);
        rb.AddTorque(transform.right * pitchAngle, ForceMode.Force);
    }
    
    /// <summary>
    /// Gizmo 그리기
    /// </summary>
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || environmentController == null) return;
        
        if (applyWind)
        {
            // 바람 방향 표시
            Vector3 windVec = environmentController.GetWindVector().normalized * 3f;
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position + Vector3.up * 3f, 
                           transform.position + Vector3.up * 3f + windVec);
        }
        
        if (applyWaves)
        {
            // 파도 높이 표시
            float waveHeight = environmentController.GetWaveHeightAt(transform.position);
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * waveHeight, 0.5f);
        }
    }
}
