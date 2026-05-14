using UnityEngine;

namespace BoatAttack
{
    /// <summary>
    /// 적군 포메이션 유형
    /// </summary>
    public enum FormationType
    {
        Concentrated,   // 집중: 한 방향에서 밀집 접근
        Wave,           // 파상: 같은 방향, 2~4줄 계단식 접근
        Diversionary,   // 양동: 3방향에서 분산 접근
        Random          // 매 에피소드 랜덤 선택
    }

    /// <summary>
    /// 스폰 위치/회전 데이터
    /// </summary>
    [System.Serializable]
    public struct SpawnData
    {
        public Vector3 position;
        public Quaternion rotation;

        public SpawnData(Vector3 pos, Quaternion rot)
        {
            position = pos;
            rotation = rot;
        }
    }

    /// <summary>
    /// 적군 포메이션 스폰 관리자
    /// Roonshot/data/extended_patterns.py의 3가지 포메이션을 C#으로 포팅
    /// - Concentrated (집중): generate_spearhead()
    /// - Wave (파상): generate_wave()
    /// - Diversionary (양동): generate_scattered()
    /// </summary>
    public class EnemyFormationSpawner : MonoBehaviour
    {
        [Header("Formation Type")]
        [Tooltip("포메이션 유형 (Random = 매 에피소드 3가지 중 랜덤 선택)")]
        public FormationType formationType = FormationType.Random;

        [Header("Concentrated (집중)")]
        [Tooltip("접근 각도 퍼짐 범위 (±도, Roonshot: 5~25°)")]
        [Range(5f, 30f)]
        public float concentratedAngleSpread = 15f;

        [Tooltip("거리 지터 범위 (m, Roonshot: -300~+500)")]
        [Range(100f, 500f)]
        public float concentratedDistJitter = 300f;

        [Tooltip("접근 각도 퍼짐을 에피소드마다 랜덤화")]
        public bool randomizeConcentratedSpread = true;

        [Header("Wave (파상)")]
        [Tooltip("웨이브 수 (Roonshot: 2~4)")]
        [Range(2, 4)]
        public int waveCount = 3;

        [Tooltip("웨이브 간 거리 (m, Roonshot: 600~1200)")]
        [Range(200f, 600f)]
        public float waveGap = 400f;

        [Tooltip("웨이브 내 좌우 퍼짐 (±도, Roonshot: 8~20°)")]
        [Range(5f, 20f)]
        public float waveLateralSpread = 12f;

        [Tooltip("웨이브 파라미터를 에피소드마다 랜덤화")]
        public bool randomizeWaveParams = true;

        [Header("Diversionary (양동)")]
        [Tooltip("공격 방향 수 (Roonshot: 3)")]
        [Range(2, 4)]
        public int diversionaryDirections = 3;

        [Tooltip("방향 각도 지터 (±도, Roonshot: 10~30°)")]
        [Range(5f, 30f)]
        public float diversionaryDirSpread = 20f;

        [Tooltip("각 클러스터 내 퍼짐 (±도, Roonshot: 8~15°)")]
        [Range(5f, 15f)]
        public float diversionaryClusterSpread = 10f;

        [Tooltip("양동 파라미터를 에피소드마다 랜덤화")]
        public bool randomizeDiversionaryParams = true;

        [Header("Island Avoidance (스폰 위치 섬 회피)")]
        [Tooltip("섬 레이어 마스크. 0이면 회피 비활성화")]
        public LayerMask islandLayerMask = 0;
        [Tooltip("스폰 위치 섬 겹침 체크 반경 (m)")]
        [Range(10f, 100f)]
        public float islandCheckRadius = 30f;
        [Tooltip("섬 회피 시 각도 조정 단계 (도)")]
        [Range(5f, 30f)]
        public float islandAvoidAngleStep = 15f;

        // 마지막으로 사용된 포메이션 (디버그용)
        private FormationType _lastFormationType;

        // 마지막 접근 방향 (아군 배치에 사용)
        private float _lastApproachAngleDeg;

        // 양동 시 각 방향의 접근 각도 (라디안)
        private float[] _lastDiversionaryAngles;

        /// <summary>
        /// 마지막으로 사용된 포메이션 유형 반환
        /// </summary>
        public FormationType GetLastFormationType() => _lastFormationType;

        /// <summary>
        /// 마지막 주 접근 방향 (도) 반환 — 아군 스폰 방향 결정에 사용
        /// </summary>
        public float GetLastApproachAngleDeg() => _lastApproachAngleDeg;

        /// <summary>
        /// 양동 포메이션의 각 방향 접근 각도 (라디안) — 다방향 아군 배치에 사용
        /// </summary>
        public float[] GetLastDiversionaryAngles() => _lastDiversionaryAngles;

        /// <summary>
        /// 후보 스폰 위치가 섬과 겹치면 각도를 조정해 빈 위치 반환.
        /// islandLayerMask == 0이면 즉시 candidate 반환.
        /// </summary>
        private Vector3 ClearIslandPos(Vector3 candidate, Vector3 origin, float dist, float angleRad)
        {
            if (islandLayerMask == 0) return candidate;

            float y = candidate.y;
            Vector3 checkPos = candidate;
            checkPos.y = 1f;
            if (!Physics.CheckSphere(checkPos, islandCheckRadius, islandLayerMask))
                return candidate;

            // 각도를 ±step씩 늘리며 비어있는 위치 탐색
            float[] deltas = { islandAvoidAngleStep, -islandAvoidAngleStep,
                               islandAvoidAngleStep * 2f, -islandAvoidAngleStep * 2f,
                               islandAvoidAngleStep * 3f, -islandAvoidAngleStep * 3f,
                               180f };
            foreach (float deg in deltas)
            {
                float a = angleRad + deg * Mathf.Deg2Rad;
                Vector3 alt = origin + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * dist;
                alt.y = y;
                Vector3 altCheck = alt; altCheck.y = 1f;
                if (!Physics.CheckSphere(altCheck, islandCheckRadius, islandLayerMask))
                    return alt;
            }

            // 모든 시도 실패 시 원래 후보 반환 (매우 드문 케이스)
            return candidate;
        }

        /// <summary>
        /// 포메이션에 따른 적군 스폰 데이터 생성
        /// </summary>
        /// <param name="motherPos">모선 위치 (방어 중심)</param>
        /// <param name="enemyCount">적군 수</param>
        /// <param name="spawnDistance">모선으로부터 스폰 거리 (m)</param>
        /// <param name="templateY">템플릿 높이 (y좌표)</param>
        /// <returns>스폰 데이터 배열</returns>
        public SpawnData[] GenerateFormation(Vector3 motherPos, int enemyCount, float spawnDistance, float templateY)
        {
            // 포메이션 유형 결정
            FormationType type = formationType;
            if (type == FormationType.Random)
            {
                int r = Random.Range(0, 3);
                type = (FormationType)r;
            }
            _lastFormationType = type;

            SpawnData[] spawns;
            switch (type)
            {
                case FormationType.Concentrated:
                    spawns = GenerateConcentrated(motherPos, enemyCount, spawnDistance, templateY);
                    break;
                case FormationType.Wave:
                    spawns = GenerateWave(motherPos, enemyCount, spawnDistance, templateY);
                    break;
                case FormationType.Diversionary:
                    spawns = GenerateDiversionary(motherPos, enemyCount, spawnDistance, templateY);
                    break;
                default:
                    spawns = GenerateConcentrated(motherPos, enemyCount, spawnDistance, templateY);
                    break;
            }

            return spawns;
        }

        /// <summary>
        /// Concentrated (집중) 포메이션 — Roonshot generate_spearhead() 포팅
        /// 한 방향에서 밀집하여 접근
        /// </summary>
        private SpawnData[] GenerateConcentrated(Vector3 motherPos, int enemyCount, float spawnDistance, float templateY)
        {
            float spreadDeg = randomizeConcentratedSpread
                ? Random.Range(5f, concentratedAngleSpread)
                : concentratedAngleSpread;
            float spreadRad = spreadDeg * Mathf.Deg2Rad / 2f;

            // 랜덤 접근 각도 (0~360°)
            float baseAngleDeg = Random.Range(0f, 360f);
            float baseAngleRad = baseAngleDeg * Mathf.Deg2Rad;
            _lastApproachAngleDeg = baseAngleDeg;
            _lastDiversionaryAngles = null;

            SpawnData[] spawns = new SpawnData[enemyCount];

            for (int i = 0; i < enemyCount; i++)
            {
                float angle = baseAngleRad + Random.Range(-spreadRad, spreadRad);
                float dist = spawnDistance + Random.Range(-concentratedDistJitter * 0.6f, concentratedDistJitter);

                Vector3 offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * dist;
                Vector3 spawnPos = motherPos + offset;
                spawnPos.y = templateY;
                spawnPos = ClearIslandPos(spawnPos, motherPos, dist, angle);

                // 모선을 바라보는 회전
                Vector3 lookDir = motherPos - spawnPos;
                lookDir.y = 0f;
                Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(lookDir, Vector3.up)
                    : Quaternion.identity;

                spawns[i] = new SpawnData(spawnPos, spawnRot);
            }

            return spawns;
        }

        /// <summary>
        /// Wave (파상) 포메이션 — Roonshot generate_wave() 포팅
        /// 같은 방향에서 계단식으로 접근 (1차 웨이브가 모선에 가장 가까움)
        /// </summary>
        private SpawnData[] GenerateWave(Vector3 motherPos, int enemyCount, float spawnDistance, float templateY)
        {
            int waves = randomizeWaveParams
                ? Random.Range(2, waveCount + 1)
                : waveCount;
            float gap = randomizeWaveParams
                ? Random.Range(waveGap * 0.7f, waveGap * 1.3f)
                : waveGap;
            float lateralDeg = randomizeWaveParams
                ? Random.Range(waveLateralSpread * 0.6f, waveLateralSpread)
                : waveLateralSpread;
            float lateralRad = lateralDeg * Mathf.Deg2Rad / 2f;

            // 모든 웨이브가 같은 방향에서 접근
            float approachAngleDeg = Random.Range(0f, 360f);
            float approachAngleRad = approachAngleDeg * Mathf.Deg2Rad;
            _lastApproachAngleDeg = approachAngleDeg;
            _lastDiversionaryAngles = null;

            int enemiesPerWave = enemyCount / waves;
            int remainder = enemyCount % waves;

            SpawnData[] spawns = new SpawnData[enemyCount];
            int spawnIdx = 0;

            for (int w = 0; w < waves; w++)
            {
                // 각 웨이브는 뒤로 갈수록 모선에서 멀어짐
                float waveDist = spawnDistance + w * gap;
                waveDist += Random.Range(-gap * 0.1f, gap * 0.1f); // 약간의 지터

                int nInWave = enemiesPerWave + (w < remainder ? 1 : 0);

                for (int i = 0; i < nInWave; i++)
                {
                    // 웨이브 내 좌우 퍼짐 (횡대 배치)
                    float lateralOffset = 0f;
                    if (nInWave > 1)
                    {
                        float frac = (float)i / (nInWave - 1) - 0.5f; // -0.5 ~ +0.5
                        lateralOffset = frac * 2f * lateralRad;
                    }

                    float angle = approachAngleRad + lateralOffset;
                    angle += Random.Range(-0.02f, 0.02f); // 미세 지터

                    float dist = waveDist + Random.Range(-50f, 50f);

                    Vector3 offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * dist;
                    Vector3 spawnPos = motherPos + offset;
                    spawnPos.y = templateY;
                    spawnPos = ClearIslandPos(spawnPos, motherPos, dist, angle);

                    Vector3 lookDir = motherPos - spawnPos;
                    lookDir.y = 0f;
                    Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;

                    spawns[spawnIdx] = new SpawnData(spawnPos, spawnRot);
                    spawnIdx++;
                }
            }

            return spawns;
        }

        /// <summary>
        /// Diversionary (양동) 포메이션 — Roonshot generate_scattered() 포팅
        /// 3방향(120°간격)에서 분산 접근
        /// </summary>
        private SpawnData[] GenerateDiversionary(Vector3 motherPos, int enemyCount, float spawnDistance, float templateY)
        {
            int numDirs = diversionaryDirections;
            float dirSpreadDeg = randomizeDiversionaryParams
                ? Random.Range(diversionaryDirSpread * 0.5f, diversionaryDirSpread)
                : diversionaryDirSpread;
            float clusterSpreadDeg = randomizeDiversionaryParams
                ? Random.Range(diversionaryClusterSpread * 0.7f, diversionaryClusterSpread)
                : diversionaryClusterSpread;
            float clusterSpreadRad = clusterSpreadDeg * Mathf.Deg2Rad / 2f;

            // 기본 각도: 360° / numDirs 간격, 랜덤 오프셋
            float baseAngleOffsetRad = Random.Range(0f, 2f * Mathf.PI);
            float[] dirAngles = new float[numDirs];
            _lastDiversionaryAngles = new float[numDirs];

            for (int d = 0; d < numDirs; d++)
            {
                float baseAngle = baseAngleOffsetRad + (2f * Mathf.PI * d / numDirs);
                float jitterRad = Random.Range(-dirSpreadDeg / 2f, dirSpreadDeg / 2f) * Mathf.Deg2Rad;
                dirAngles[d] = baseAngle + jitterRad;
                _lastDiversionaryAngles[d] = dirAngles[d];
            }

            // 주 접근 방향 = 첫 번째 방향 (아군 배치 기준)
            _lastApproachAngleDeg = baseAngleOffsetRad * Mathf.Rad2Deg;

            // 적군을 방향별로 균등 분배
            int enemiesPerDir = enemyCount / numDirs;
            int dirRemainder = enemyCount % numDirs;

            SpawnData[] spawns = new SpawnData[enemyCount];
            int spawnIdx = 0;

            for (int d = 0; d < numDirs; d++)
            {
                int nInCluster = enemiesPerDir + (d < dirRemainder ? 1 : 0);
                float clusterBaseDist = spawnDistance + Random.Range(-spawnDistance * 0.1f, spawnDistance * 0.1f);

                for (int i = 0; i < nInCluster; i++)
                {
                    // 클러스터 내 좌우 퍼짐
                    float lateralOffset = 0f;
                    if (nInCluster > 1)
                    {
                        float frac = (float)i / (nInCluster - 1) - 0.5f;
                        lateralOffset = frac * 2f * clusterSpreadRad;
                    }

                    float angle = dirAngles[d] + lateralOffset;
                    angle += Random.Range(-0.03f, 0.03f); // 미세 지터

                    float dist = clusterBaseDist + Random.Range(-200f, 300f);

                    Vector3 offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * dist;
                    Vector3 spawnPos = motherPos + offset;
                    spawnPos.y = templateY;
                    spawnPos = ClearIslandPos(spawnPos, motherPos, dist, angle);

                    Vector3 lookDir = motherPos - spawnPos;
                    lookDir.y = 0f;
                    Quaternion spawnRot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;

                    spawns[spawnIdx] = new SpawnData(spawnPos, spawnRot);
                    spawnIdx++;
                }
            }

            return spawns;
        }
    }
}
