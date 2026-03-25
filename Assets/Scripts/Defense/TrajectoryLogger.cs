using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatAttack
{
    /// <summary>
    /// 아군/적군 선박 경로를 매 스텝 기록하고 에피소드 종료 시 CSV로 저장.
    /// DefenseEnvController에서 RecordStep() / SaveAndReset() 호출.
    /// </summary>
    public class TrajectoryLogger : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("저장 활성화")]
        public bool enableLogging = false;

        [Tooltip("N스텝마다 기록 (1=매 스텝, 5=5스텝마다)")]
        public int recordInterval = 1;

        [Tooltip("저장 경로 (프로젝트 루트 기준)")]
        public string savePath = @"C:\Users\ANSL\Desktop\논문 그림용\trajectories";

        [Tooltip("최근 N개 에피소드만 보관 (0=무제한)")]
        public int maxFiles = 20;

        // 한 프레임의 기록
        private struct FrameRecord
        {
            public int step;
            public int id;        // 선박 고유 ID (아군: pair*2+agent, 적군: 100+idx)
            public string team;   // "ally" or "enemy"
            public float x, z;
            public float heading;
            public float speed;
            public int pairIdx;   // 아군만: 소속 쌍 번호
            public bool neutralized;
        }

        private List<FrameRecord> _records = new List<FrameRecord>(10000);
        private int _episodeNumber;
        private string _formationType;
        private string _endReason;

        /// <summary>에피소드 시작 시 호출</summary>
        public void BeginEpisode(int episodeNumber, string formationType)
        {
            _records.Clear();
            _episodeNumber = episodeNumber;
            _formationType = formationType ?? "unknown";
            _endReason = "";
        }

        /// <summary>매 스텝 호출 — 모든 활성 선박 위치 기록</summary>
        public void RecordStep(int step, LaunchZoneManager lzm, GameObject[] enemyPool,
            System.Collections.Generic.HashSet<GameObject> neutralizedEnemies)
        {
            if (!enableLogging) return;
            if (recordInterval > 1 && step % recordInterval != 0) return;

            // 아군 기록
            if (lzm != null && lzm.IsInitialized)
            {
                int poolCount = lzm.GetCurrentPoolCount();
                for (int i = 0; i < poolCount; i++)
                {
                    DefensePair pair = lzm.GetPair(i);
                    if (pair == null || !pair.isActive) continue;

                    RecordAgent(step, pair.agent1, "ally", i, 0, pair.isDisarmed);
                    RecordAgent(step, pair.agent2, "ally", i, 1, pair.isDisarmed);
                }
            }

            // 적군 기록
            if (enemyPool != null)
            {
                for (int i = 0; i < enemyPool.Length; i++)
                {
                    if (enemyPool[i] == null || !enemyPool[i].activeSelf) continue;
                    bool neutralized = neutralizedEnemies != null && neutralizedEnemies.Contains(enemyPool[i]);

                    Transform t = enemyPool[i].transform;
                    Vector3 pos = t.position;
                    if (pos.y < -100f) continue; // HIDDEN_POS

                    Rigidbody rb = enemyPool[i].GetComponent<Rigidbody>();
                    float speed = 0f;
                    if (rb != null)
                    {
                        Vector3 vel = rb.velocity; vel.y = 0f;
                        speed = vel.magnitude;
                    }

                    _records.Add(new FrameRecord
                    {
                        step = step,
                        id = 100 + i,
                        team = "enemy",
                        x = pos.x,
                        z = pos.z,
                        heading = t.eulerAngles.y,
                        speed = speed,
                        pairIdx = -1,
                        neutralized = neutralized
                    });
                }
            }
        }

        private void RecordAgent(int step, DefenseAgent agent, string team, int pairIdx, int agentIdx, bool neutralized)
        {
            if (agent == null) return;
            Vector3 pos = agent.transform.position;
            if (pos.y < -100f) return;

            float speed = 0f;
            if (agent._engine != null && agent._engine.RB != null)
            {
                Vector3 vel = agent._engine.RB.velocity; vel.y = 0f;
                speed = vel.magnitude;
            }

            _records.Add(new FrameRecord
            {
                step = step,
                id = pairIdx * 2 + agentIdx,
                team = team,
                x = pos.x,
                z = pos.z,
                heading = agent.transform.eulerAngles.y,
                speed = speed,
                pairIdx = pairIdx,
                neutralized = neutralized
            });
        }

        /// <summary>에피소드 종료 시 CSV 저장</summary>
        public void SaveAndReset(string endReason, Vector3 motherShipPos)
        {
            if (!enableLogging || _records.Count == 0) return;
            _endReason = endReason;

            string dir = Path.IsPathRooted(savePath)
                ? savePath
                : Path.Combine(Application.dataPath, "..", savePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filename = $"ep{_episodeNumber:D4}_{_formationType}_{_endReason}.csv";
            string filepath = Path.Combine(dir, filename);

            var sb = new StringBuilder();
            // 헤더: 메타데이터 주석
            sb.AppendLine($"# episode={_episodeNumber},formation={_formationType},end={_endReason},motherX={motherShipPos.x:F1},motherZ={motherShipPos.z:F1}");
            sb.AppendLine("step,id,team,x,z,heading,speed,pairIdx,neutralized");

            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                sb.AppendLine($"{r.step},{r.id},{r.team},{r.x:F2},{r.z:F2},{r.heading:F1},{r.speed:F2},{r.pairIdx},{(r.neutralized ? 1 : 0)}");
            }

            File.WriteAllText(filepath, sb.ToString());
            Debug.Log($"[TrajectoryLogger] Saved {_records.Count} records → {filepath}");

            // 오래된 파일 정리
            if (maxFiles > 0)
                CleanupOldFiles(dir);

            _records.Clear();
        }

        private void CleanupOldFiles(string dir)
        {
            var files = new DirectoryInfo(dir).GetFiles("ep*.csv");
            if (files.Length <= maxFiles) return;

            System.Array.Sort(files, (a, b) => a.CreationTime.CompareTo(b.CreationTime));
            int toDelete = files.Length - maxFiles;
            for (int i = 0; i < toDelete; i++)
                files[i].Delete();
        }
    }
}
