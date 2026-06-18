using UnityEngine;
using WaterSystem;
using WaterSystem.Data;

[ExecuteAlways]
public class WindzoneExtended : MonoBehaviour
{
    public WindZone zone;
    // vector for wind, x = radian, y = strength, z = turbulence, w = frequency
    private Vector4 _windVector = Vector4.zero;
    public string shaderProp = "_WindZone_Vector";
    public float test;

    // 물리용 정적 데이터: Engine.cs 등에서 참조
    /// <summary>바람 방향 (XZ 평면, 정규화됨)</summary>
    public static Vector3 WindDirection { get; private set; } = Vector3.forward;
    /// <summary>바람 속도 (m/s)</summary>
    public static float WindSpeed { get; private set; } = 0f;
    /// <summary>초기화 완료 여부</summary>
    public static bool Initialized { get; private set; } = false;

    private static WindzoneExtended _instance;

    /// <summary>
    /// 도메인 리로드 비활성(Enter Play Mode Settings) 시에도 정적 변수 리셋 보장
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        WindDirection = Vector3.forward;
        WindSpeed = 0f;
        Initialized = false;
        _instance = null;
    }

    void OnEnable()
    {
        _instance = this;
        // zone이 할당되지 않았으면 자동 탐색
        if (zone == null)
            zone = GetComponent<WindZone>();
        if (zone == null)
            zone = FindObjectOfType<WindZone>();
    }

    // Update is called once per frame
    void Update()
    {
        if (!zone) return;

        //Do Wind things
        SetDirection();

        Shader.SetGlobalVector(shaderProp, _windVector);

        // 물리용 데이터 갱신
        float angle = _windVector.x; // radians
        WindDirection = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        WindSpeed = zone.windMain;
        Initialized = true;
    }

    void SetDirection()
    {
        var vec = transform.forward;
        vec.y = 0;
        var sign = Vector3.Dot(Vector3.left, vec) < 0 ? -1f : 1f;
        _windVector.x = (Vector3.Angle(Vector3.forward, vec) / 180f * Mathf.PI) * sign;
        test = _windVector.x;
    }

    /// <summary>
    /// WindzoneExtended가 씬에 없을 때 Engine.cs에서 호출하여 바람 데이터 직접 초기화.
    /// WindZone이 씬에 없으면 랜덤 기본 바람을 생성.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (Initialized) return;

        // 씬에서 WindZone 찾기
        var wz = FindObjectOfType<WindZone>();
        if (wz != null)
        {
            var forward = wz.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;

            WindDirection = forward;
            WindSpeed = wz.windMain;
        }
        else
        {
            // WindZone 없음 → 랜덤 기본 바람 (에피소드마다 변화 가능)
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            WindDirection = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            WindSpeed = Random.Range(5f, 15f); // 5~15 m/s (보퍼트 3~7)
        }
        Initialized = true;
        Debug.Log($"[Wind] EnsureInitialized: dir={WindDirection}, speed={WindSpeed:F2} m/s, hasWindZone={FindObjectOfType<WindZone>() != null}");
    }

    /// <summary>
    /// _instance가 없으면 씬의 WindZone에 자동 부착
    /// </summary>
    static void EnsureInstance()
    {
        if (_instance != null) return;
        var wz = FindObjectOfType<WindZone>();
        if (wz != null && wz.GetComponent<WindzoneExtended>() == null)
        {
            _instance = wz.gameObject.AddComponent<WindzoneExtended>();
            Debug.Log("[WindzoneExtended] 씬의 WindZone에 자동 부착됨");
        }
    }

    /// <summary>
    /// 에피소드 리셋 시 호출하여 바람 방향/세기를 재랜덤화 (도메인 랜덤화)
    /// </summary>
    public static void RandomizeWind() => RandomizeWind(-1f, -1f);

    /// <summary>
    /// 바람 방향/세기 랜덤화. spdMin/spdMax가 음수면 기존 fallback 사용.
    /// </summary>
    public static void RandomizeWind(float spdMin, float spdMax)
    {
        EnsureInstance();

        // 매 에피소드 방향/속도 랜덤화
        float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
        WindDirection = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

        if (spdMin < 0f || spdMax < 0f)
        {
            spdMin = 3f; spdMax = 10f;
            if (_instance != null)
            {
                spdMin = _instance.windSpeedMin;
                spdMax = _instance.windSpeedMax;
            }
        }
        WindSpeed = Random.Range(spdMin, spdMax);

        // WindZone이 있으면 동기화 (비주얼용)
        var wz = FindObjectOfType<WindZone>();
        if (wz != null)
        {
            wz.windMain = WindSpeed;
            wz.transform.forward = WindDirection;
        }
        Initialized = true;

        // 파도 랜덤화
        RandomizeWaves();

        Debug.Log($"[Wind] RandomizeWind: dir={WindDirection}, speed={WindSpeed:F2} m/s, Water={Water.Instance != null}");
    }

    [Header("Wind Randomization")]
    public float windSpeedMin = 5f;
    public float windSpeedMax = 15f;

    [Header("Wave Randomization")]
    [Tooltip("파도 랜덤화 활성화 (false면 인스펙터 파도 설정 유지)")]
    public bool enableWaveRandomization = true;
    public float waveAmplitudeMin = 4.5f;    // 높이 ×1.5
    public float waveAmplitudeMax = 9.0f;
    public float waveWavelengthMin = 16.875f; // 주기 ×0.5 (33.75→16.875)
    public float waveWavelengthMax = 41.25f;  // 82.5→41.25

    /// <summary>
    /// 에피소드마다 파도 방향/높이/파장 랜덤화 (Water.cs에서 GPU/CPU 모두 처리)
    /// </summary>
    public static void RandomizeWaves()
    {
        if (Water.Instance == null) return;

        // 파도 랜덤화 비활성화 시 인스펙터 설정 유지
        if (_instance != null && !_instance.enableWaveRandomization) return;

        float ampMin = 4.5f, ampMax = 9.0f, lenMin = 16.875f, lenMax = 41.25f;  // 높이×1.5, 주기×0.5
        if (_instance != null)
        {
            ampMin = _instance.waveAmplitudeMin;
            ampMax = _instance.waveAmplitudeMax;
            lenMin = _instance.waveWavelengthMin;
            lenMax = _instance.waveWavelengthMax;
        }

        Water.Instance.RandomizeWaves(ampMin, ampMax, lenMin, lenMax);
    }
}
