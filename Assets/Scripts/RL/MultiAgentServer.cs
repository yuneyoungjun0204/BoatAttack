using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[Serializable]
public class ShipData
{
    public string id;            // 선박 ID (GameObject name)
    public string tag;           // Unity Tag (Friendly, Enemy, Neutral)
    public float[] position;     // [x, y, z]
    public float[] velocity;     // [vx, vy, vz]
    public float[] rotation;     // [pitch, yaw, roll]
    public float health;
    public float ammo;
    public float speed;
}

[Serializable]
public class MultiAgentMessage
{
    public List<ShipData> ships = new List<ShipData>();
    public int frameCount;
    public float time;
}

[Serializable]
public class ActionMessage
{
    public string shipId;
    public float throttle;
    public float steering;
    public bool fire;
}

public class MultiAgentServer : MonoBehaviour
{
    [Header("Server Settings")]
    public int port = 9876;
    
    [Header("Ship Detection")]
    public string[] shipTags = { "Friendly", "attack_boat", "MotherShip" };
    public float sendInterval = 0.1f;  // 10Hz
    
    private TcpListener listener;
    private TcpClient client;
    private NetworkStream stream;
    private Thread serverThread;
    private Thread receiveThread;
    
    private bool isRunning = false;
    private object lockObj = new object();
    
    // 수신된 액션 큐 (메인 스레드에서 처리)
    private Queue<ActionMessage> actionQueue = new Queue<ActionMessage>();
    
    private float nextSendTime;
    private int frameCounter = 0;

    void Start()
    {
        StartServer();
        nextSendTime = Time.time;
    }

    void OnDestroy()
    {
        StopServer();
    }

    void Update()
    {
        // 액션 처리 (메인 스레드)
        lock (lockObj)
        {
            while (actionQueue.Count > 0)
            {
                ActionMessage action = actionQueue.Dequeue();
                ApplyAction(action);
            }
        }
    }

    void FixedUpdate()
    {
        // 주기적으로 모든 선박 정보 전송
        if (Time.time >= nextSendTime && client != null && client.Connected)
        {
            SendAllShipsData();
            nextSendTime = Time.time + sendInterval;
            frameCounter++;
        }
    }

    private void StartServer()
    {
        isRunning = true;
        serverThread = new Thread(ServerLoop);
        serverThread.IsBackground = true;
        serverThread.Start();
        
        Debug.Log($"[MultiAgentServer] Server started on port {port}");
    }

    private void StopServer()
    {
        isRunning = false;
        
        if (stream != null) stream.Close();
        if (client != null) client.Close();
        if (listener != null) listener.Stop();
        
        if (serverThread != null && serverThread.IsAlive)
            serverThread.Join(1000);
        if (receiveThread != null && receiveThread.IsAlive)
            receiveThread.Join(1000);
        
        Debug.Log("[MultiAgentServer] Server stopped");
    }

    private void ServerLoop()
    {
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Debug.Log($"[MultiAgentServer] Listening on port {port}...");

            while (isRunning)
            {
                // 클라이언트가 없거나 끊어졌으면 새 연결 대기
                if ((client == null || !client.Connected) && listener.Pending())
                {
                    // 이전 연결 정리
                    CleanupClient();
                    
                    // 새 클라이언트 수락
                    client = listener.AcceptTcpClient();
                    stream = client.GetStream();
                    Debug.Log("[MultiAgentServer] Client connected!");

                    // 수신 스레드 시작
                    if (receiveThread != null && receiveThread.IsAlive)
                    {
                        receiveThread.Join(500);  // 기존 스레드 종료 대기
                    }
                    receiveThread = new Thread(ReceiveLoop);
                    receiveThread.IsBackground = true;
                    receiveThread.Start();
                }
                Thread.Sleep(100);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MultiAgentServer] Server error: {e.Message}");
        }
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[4096];
        
        try
        {
            while (isRunning && client != null && client.Connected)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // 연결 종료
                    Debug.LogWarning("[MultiAgentServer] Client disconnected (0 bytes)");
                    break;
                }
                
                if (bytesRead > 0)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    
                    try
                    {
                        ActionMessage action = JsonUtility.FromJson<ActionMessage>(json);
                        
                        // 큐에 추가 (메인 스레드에서 처리)
                        lock (lockObj)
                        {
                            actionQueue.Enqueue(action);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[MultiAgentServer] JSON parse error: {e.Message}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiAgentServer] Receive error: {e.Message}");
        }
        finally
        {
            Debug.Log("[MultiAgentServer] Receive loop ended - cleaning up client");
            CleanupClient();
        }
    }

    private void SendAllShipsData()
    {
        if (stream == null || !stream.CanWrite)
            return;

        // 연결 상태 확인
        if (client == null || !client.Connected)
        {
            Debug.LogWarning("[MultiAgentServer] Client disconnected - waiting for reconnection...");
            CleanupClient();
            return;
        }

        try
        {
            MultiAgentMessage msg = new MultiAgentMessage();
            msg.frameCount = frameCounter;
            msg.time = Time.time;

            // 모든 태그의 선박 수집
            int totalShipsFound = 0;
            foreach (string tagName in shipTags)
            {
                GameObject[] ships = GameObject.FindGameObjectsWithTag(tagName);
                
                if (frameCounter % 100 == 0)  // 100 프레임마다 로그
                {
                    Debug.Log($"[MultiAgentServer] Tag '{tagName}': Found {ships.Length} ships");
                }
                
                foreach (GameObject ship in ships)
                {
                    ShipData shipData = CollectShipData(ship, tagName);
                    if (shipData != null)
                    {
                        msg.ships.Add(shipData);
                        totalShipsFound++;
                    }
                }
            }
            
            if (frameCounter % 100 == 0)  // 100 프레임마다 로그
            {
                Debug.Log($"[MultiAgentServer] Total ships collected: {totalShipsFound}");
            }

            // JSON 직렬화 및 전송
            string json = JsonUtility.ToJson(msg);
            byte[] data = Encoding.UTF8.GetBytes(json + "\n");
            
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiAgentServer] Send error: {e.Message}");
            CleanupClient();  // 연결 끊김 시 정리
        }
    }
    
    private void CleanupClient()
    {
        // 클라이언트 정리 (재연결 대기)
        try
        {
            if (stream != null)
            {
                stream.Close();
                stream = null;
            }
            if (client != null)
            {
                client.Close();
                client = null;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiAgentServer] Cleanup error: {e.Message}");
        }
    }

    private ShipData CollectShipData(GameObject ship, string tag)
    {
        if (ship == null)
            return null;

        ShipData data = new ShipData();
        data.id = ship.name;
        data.tag = tag;

        // 위치
        data.position = new float[] {
            ship.transform.position.x,
            ship.transform.position.y,
            ship.transform.position.z
        };

        // 회전 (Euler angles)
        data.rotation = new float[] {
            ship.transform.eulerAngles.x,
            ship.transform.eulerAngles.y,
            ship.transform.eulerAngles.z
        };

        // 속도
        Rigidbody rb = ship.GetComponent<Rigidbody>();
        if (rb != null)
        {
            data.velocity = new float[] {
                rb.velocity.x,
                rb.velocity.y,
                rb.velocity.z
            };
            data.speed = rb.velocity.magnitude;
        }
        else
        {
            data.velocity = new float[] { 0, 0, 0 };
            data.speed = 0f;
        }

        // 상태 정보 (추후 실제 컴포넌트 연결)
        data.health = 100f;  // TODO: 실제 체력 시스템 연결
        data.ammo = 50f;     // TODO: 실제 무기 시스템 연결

        return data;
    }

    private void ApplyAction(ActionMessage action)
    {
        // 선박 ID로 GameObject 찾기
        GameObject ship = GameObject.Find(action.shipId);
        
        if (ship == null)
        {
            Debug.LogWarning($"[MultiAgentServer] Ship not found: {action.shipId}");
            return;
        }

        // Boat 컴포넌트 찾기 (BoatAttack.Boat)
        var boat = ship.GetComponent<BoatAttack.Boat>();
        if (boat != null && boat.engine != null)
        {
            // Engine 직접 제어
            boat.engine.Accelerate(action.throttle);
            boat.engine.Turn(action.steering);
            
            // 디버그 로그
            Debug.Log($"[MultiAgentServer] Action applied to {action.shipId}: Throttle={action.throttle:F2}, Steering={action.steering:F2}");
        }
        else
        {
            Debug.LogWarning($"[MultiAgentServer] Boat/Engine not found on {action.shipId}");
        }
    }
}
