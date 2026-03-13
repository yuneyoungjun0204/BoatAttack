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
    private static bool _debugLogged = false;

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
        _debugLogged = false;
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
    /// 에피소드 리셋 시 호출하여 바람 방향/세기를 재랜덤화 (도메인 랜덤화)
    /// </summary>
    public static void RandomizeWind() => RandomizeWind(-1f, -1f);

    /// <summary>
    /// 바람 방향/세기 랜덤화. spdMin/spdMax가 음수면 기존 fallback 사용.
    /// </summary>
    public static void RandomizeWind(float spdMin, float spdMax)
    {
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
    public float waveAmplitudeMin = 0.1f;
    public float waveAmplitudeMax = 1.5f;
    public float waveWavelengthMin = 2f;
    public float waveWavelengthMax = 10f;

    /// <summary>
    /// 에피소드마다 파도 방향/높이/파장 랜덤화 (Water.cs에서 GPU/CPU 모두 처리)
    /// </summary>
    public static void RandomizeWaves()
    {
        if (Water.Instance == null) return;

        float ampMin = 0.1f, ampMax = 1.5f, lenMin = 2f, lenMax = 10f;
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
