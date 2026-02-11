using System;
using UnityEngine;

[Serializable]
public class ObservationData
{
    public float[] position;      // [x, y, z]
    public float[] velocity;      // [vx, vy, vz]
    public float[] rotation;      // [pitch, yaw, roll]
    public float[] radarData;     // 레이더 감지 정보
    public float health;
    public float ammo;
    public int stepCount;
    public bool done;
}

[Serializable]
public class ActionData
{
    public float throttle;   // 0~1
    public float steering;   // -1~1
    public bool fire;
}

public class RLAgent : MonoBehaviour
{
    [Header("References")]
    public SocketServer socketServer;
    public Transform targetTransform;

    [Header("RL Settings")]
    public float sendInterval = 0.1f;  // 10Hz
    private float nextSendTime = 0f;

    [Header("Agent State")]
    public float throttle = 0f;
    public float steering = 0f;
    public bool fire = false;

    private ObservationData obs = new ObservationData();
    private int stepCount = 0;

    void Start()
    {
        if (socketServer == null)
        {
            socketServer = FindObjectOfType<SocketServer>();
        }
    }

    void FixedUpdate()
    {
        // 관측 전송 (일정 주기마다)
        if (Time.time >= nextSendTime)
        {
            CollectObservation();
            SendObservation();
            nextSendTime = Time.time + sendInterval;
            stepCount++;
        }
    }

    private void CollectObservation()
    {
        // 위치
        obs.position = new float[] {
            transform.position.x,
            transform.position.y,
            transform.position.z
        };

        // 속도 (Rigidbody 있다고 가정)
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            obs.velocity = new float[] {
                rb.velocity.x,
                rb.velocity.y,
                rb.velocity.z
            };
        }
        else
        {
            obs.velocity = new float[] { 0, 0, 0 };
        }

        // 회전 (Euler angles)
        obs.rotation = new float[] {
            transform.eulerAngles.x,
            transform.eulerAngles.y,
            transform.eulerAngles.z
        };

        // 레이더 데이터 (간단한 예시)
        obs.radarData = GetRadarData();

        // 상태 정보
        obs.health = 100f;  // TODO: 실제 체력 연결
        obs.ammo = 50f;     // TODO: 실제 탄약 연결
        obs.stepCount = stepCount;
        obs.done = false;   // TODO: 에피소드 종료 조건
    }

    private float[] GetRadarData()
    {
        // 8방향 레이캐스트 (간단한 예시)
        float[] radar = new float[8];
        float rayDistance = 100f;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f;
            Vector3 direction = Quaternion.Euler(0, angle, 0) * transform.forward;
            
            RaycastHit hit;
            if (Physics.Raycast(transform.position, direction, out hit, rayDistance))
            {
                radar[i] = hit.distance / rayDistance;  // 정규화된 거리
            }
            else
            {
                radar[i] = 1.0f;  // 아무것도 없음
            }
        }

        return radar;
    }

    private void SendObservation()
    {
        string json = JsonUtility.ToJson(obs);
        socketServer?.SendObservation(json);
    }

    // Python에서 액션을 받아서 적용
    public void ApplyAction(ActionData action)
    {
        throttle = Mathf.Clamp01(action.throttle);
        steering = Mathf.Clamp(action.steering, -1f, 1f);
        fire = action.fire;

        // TODO: 실제 배 컨트롤러에 적용
        Debug.Log($"[RLAgent] Throttle: {throttle:F2}, Steering: {steering:F2}, Fire: {fire}");
    }
}
