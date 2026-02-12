using UnityEngine;
using WaterSystem;  // BoatAttack의 Water System

/// <summary>
/// 환경 제어 (파도, 바람)
/// Inspector에서 쉽게 조절 가능
/// </summary>
public class EnvironmentController : MonoBehaviour
{
    [Header("=== 파도 설정 ===")]
    [Tooltip("파도 강도 (0 = 잔잔함, 1 = 보통, 2 = 거침, 3+ = 폭풍)")]
    [Range(0f, 5f)]
    public float waveStrength = 1.0f;
    
    [Tooltip("파도 속도 (0.5 = 느림, 1.0 = 보통, 2.0 = 빠름)")]
    [Range(0.1f, 3f)]
    public float waveSpeed = 1.0f;
    
    [Tooltip("파도 방향 (도)")]
    [Range(0f, 360f)]
    public float waveDirection = 0f;
    
    [Header("=== 바람 설정 ===")]
    [Tooltip("바람 강도 (m/s)")]
    [Range(0f, 30f)]
    public float windStrength = 5.0f;
    
    [Tooltip("바람 방향 (도, 0 = 북쪽)")]
    [Range(0f, 360f)]
    public float windDirection = 0f;
    
    [Tooltip("바람 난류 (0 = 일정, 1 = 변화무쌍)")]
    [Range(0f, 1f)]
    public float windTurbulence = 0.3f;
    
    [Header("=== 랜덤 날씨 ===")]
    [Tooltip("시작 시 랜덤 날씨 적용")]
    public bool randomizeOnStart = false;
    
    [Tooltip("실시간 날씨 변화")]
    public bool dynamicWeather = false;
    
    [Tooltip("날씨 변화 주기 (초)")]
    [Range(10f, 300f)]
    public float weatherChangeInterval = 60f;
    
    [Header("=== 프리셋 ===")]
    public WeatherPreset currentPreset = WeatherPreset.Calm;
    
    public enum WeatherPreset
    {
        Calm,        // 잔잔함
        Light,       // 약간
        Moderate,    // 보통
        Rough,       // 거침
        Storm        // 폭풍
    }
    
    // 내부 변수
    private Water waterInstance;
    private float nextWeatherChangeTime;
    private Vector3 currentWindVector;
    
    void Start()
    {
        // Water 시스템 찾기
        waterInstance = FindObjectOfType<Water>();
        
        if (waterInstance == null)
        {
            Debug.LogWarning("[EnvironmentController] Water instance not found!");
        }
        
        // 초기 설정
        if (randomizeOnStart)
        {
            RandomizeWeather();
        }
        else
        {
            ApplyPreset(currentPreset);
        }
        
        ApplySettings();
        
        nextWeatherChangeTime = Time.time + weatherChangeInterval;
    }
    
    void Update()
    {
        // 동적 날씨 변화
        if (dynamicWeather && Time.time >= nextWeatherChangeTime)
        {
            SmoothWeatherChange();
            nextWeatherChangeTime = Time.time + weatherChangeInterval;
        }
        
        // 실시간 적용 (Inspector 수정 시)
        if (Application.isEditor)
        {
            ApplySettings();
        }
    }
    
    /// <summary>
    /// 설정 적용
    /// </summary>
    public void ApplySettings()
    {
        ApplyWaveSettings();
        ApplyWindSettings();
    }
    
    /// <summary>
    /// 파도 설정 적용
    /// </summary>
    void ApplyWaveSettings()
    {
        if (waterInstance == null) return;
        
        // BoatAttack Water System 설정
        // (실제 Water 컴포넌트의 public 변수에 따라 조정 필요)
        waterInstance.transform.position = new Vector3(0, 0, 0);
        
        // 파도 파라미터 (Water 스크립트에 따라 다름)
        // 예: waterInstance.waveAmplitude = waveStrength;
        //     waterInstance.waveSpeed = waveSpeed;
        
        Debug.Log($"[Environment] Wave - Strength: {waveStrength}, Speed: {waveSpeed}, Dir: {waveDirection}°");
    }
    
    /// <summary>
    /// 바람 설정 적용
    /// </summary>
    void ApplyWindSettings()
    {
        // 바람 벡터 계산 (Unity 좌표계: Y축 위, X/Z 평면)
        float radians = windDirection * Mathf.Deg2Rad;
        currentWindVector = new Vector3(
            Mathf.Sin(radians),
            0f,
            Mathf.Cos(radians)
        ) * windStrength;
        
        // 난류 추가
        if (windTurbulence > 0)
        {
            currentWindVector += new Vector3(
                Random.Range(-windTurbulence, windTurbulence),
                0f,
                Random.Range(-windTurbulence, windTurbulence)
            ) * windStrength;
        }
        
        Debug.Log($"[Environment] Wind - Strength: {windStrength}m/s, Dir: {windDirection}°, Turbulence: {windTurbulence}");
    }
    
    /// <summary>
    /// 프리셋 적용
    /// </summary>
    public void ApplyPreset(WeatherPreset preset)
    {
        currentPreset = preset;
        
        switch (preset)
        {
            case WeatherPreset.Calm:
                waveStrength = 0.3f;
                waveSpeed = 0.5f;
                windStrength = 2f;
                windTurbulence = 0.1f;
                break;
            
            case WeatherPreset.Light:
                waveStrength = 0.8f;
                waveSpeed = 0.8f;
                windStrength = 5f;
                windTurbulence = 0.2f;
                break;
            
            case WeatherPreset.Moderate:
                waveStrength = 1.5f;
                waveSpeed = 1.0f;
                windStrength = 10f;
                windTurbulence = 0.3f;
                break;
            
            case WeatherPreset.Rough:
                waveStrength = 2.5f;
                waveSpeed = 1.5f;
                windStrength = 15f;
                windTurbulence = 0.5f;
                break;
            
            case WeatherPreset.Storm:
                waveStrength = 4.0f;
                waveSpeed = 2.0f;
                windStrength = 25f;
                windTurbulence = 0.8f;
                break;
        }
        
        ApplySettings();
        Debug.Log($"[Environment] Preset applied: {preset}");
    }
    
    /// <summary>
    /// 랜덤 날씨
    /// </summary>
    public void RandomizeWeather()
    {
        waveStrength = Random.Range(0.5f, 3.0f);
        waveSpeed = Random.Range(0.5f, 2.0f);
        waveDirection = Random.Range(0f, 360f);
        
        windStrength = Random.Range(3f, 20f);
        windDirection = Random.Range(0f, 360f);
        windTurbulence = Random.Range(0.1f, 0.6f);
        
        ApplySettings();
        Debug.Log("[Environment] Weather randomized");
    }
    
    /// <summary>
    /// 부드러운 날씨 변화
    /// </summary>
    void SmoothWeatherChange()
    {
        float changeAmount = 0.2f;
        
        waveStrength = Mathf.Clamp(waveStrength + Random.Range(-changeAmount, changeAmount), 0.3f, 4.0f);
        waveSpeed = Mathf.Clamp(waveSpeed + Random.Range(-0.1f, 0.1f), 0.3f, 2.5f);
        windStrength = Mathf.Clamp(windStrength + Random.Range(-3f, 3f), 1f, 25f);
        windDirection = (windDirection + Random.Range(-30f, 30f)) % 360f;
        
        ApplySettings();
        Debug.Log("[Environment] Weather changed smoothly");
    }
    
    /// <summary>
    /// 현재 바람 벡터 가져오기 (다른 스크립트에서 사용)
    /// </summary>
    public Vector3 GetWindVector()
    {
        return currentWindVector;
    }
    
    /// <summary>
    /// 특정 위치의 파도 높이 계산 (간이 구현)
    /// </summary>
    public float GetWaveHeightAt(Vector3 position)
    {
        if (waterInstance == null) return 0f;
        
        // 간단한 사인 파형 (실제로는 Water 시스템의 함수 사용)
        float waveOffset = (position.x + position.z) * 0.1f + Time.time * waveSpeed;
        float height = Mathf.Sin(waveOffset) * waveStrength * 0.5f;
        
        return height;
    }
    
    /// <summary>
    /// Gizmo 그리기 (바람 방향 표시)
    /// </summary>
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        
        // 바람 방향 화살표
        Gizmos.color = Color.cyan;
        Vector3 arrowStart = transform.position + Vector3.up * 5f;
        Gizmos.DrawLine(arrowStart, arrowStart + currentWindVector.normalized * 10f);
        
        // 파도 방향
        Gizmos.color = Color.blue;
        float waveRad = waveDirection * Mathf.Deg2Rad;
        Vector3 waveDir = new Vector3(Mathf.Sin(waveRad), 0, Mathf.Cos(waveRad));
        Gizmos.DrawLine(arrowStart, arrowStart + waveDir * 8f);
    }
}
