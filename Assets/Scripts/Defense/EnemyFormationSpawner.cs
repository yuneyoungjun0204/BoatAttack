using UnityEngine;
using System.Collections.Generic;

namespace BoatAttack
{
    /// <summary>
    /// 적군 포메이션 타입
    /// </summary>
    public enum EnemyFormation
    {
        Concentrated,   // 집중: 한 방향 밀집 클러스터
        Wave,           // 파상: 같은 방향, 여러 웨이브
        Diversionary,   // 양동: 3방향 분산
        Random          // 에피소드마다 랜덤 선택
    }

    /// <summary>
    /// 적군 스폰 데이터
    /// </summary>
    public struct EnemySpawnData
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    /// <summary>
    /// 적군 포메이션 스포너 (extended_patterns.py C# 포팅)
    /// 집중/파상/양동 3가지 포메이션을 Unity 스케일에 맞게 구현
    /// </summary>
    public static class EnemyFormationSpawner
    {
        /// <summary>
        /// 포메이션 생성 (Random이면 랜덤 선택)
        /// </summary>
        public static List<EnemySpawnData> Generate(EnemyFormation formation,
            Vector3 motherPos, int count, float baseDist, float baseY)
        {
            if (formation == EnemyFormation.Random)
            {
                int pick = UnityEngine.Random.Range(0, 3);
                formation = (EnemyFormation)pick;
            }

            switch (formation)
            {
                case EnemyFormation.Concentrated:
                    return GenerateConcentrated(motherPos, count, baseDist, baseY);
                case EnemyFormation.Wave:
                    return GenerateWave(motherPos, count, baseDist, baseY);
                case EnemyFormation.Diversionary:
                    return GenerateDiversionary(motherPos, count, baseDist, baseY);
                default:
                    return GenerateConcentrated(motherPos, count, baseDist, baseY);
            }
        }

        /// <summary>
        /// 집중 포메이션: baseAngle ±(5~25deg) 밀집 클러스터
        /// Python: concentrated_attack — 좁은 각도 범위에 모든 적 배치
        /// </summary>
        public static List<EnemySpawnData> GenerateConcentrated(
            Vector3 motherPos, int count, float baseDist, float baseY)
        {
            var result = new List<EnemySpawnData>();
            if (count <= 0) return result;

            // 기본 접근 각도 (랜덤)
            float baseAngle = UnityEngine.Random.Range(0f, 360f);
            // 밀집 범위: ±5~25°
            float spreadAngle = UnityEngine.Random.Range(5f, 25f);

            for (int i = 0; i < count; i++)
            {
                float angle = baseAngle + UnityEngine.Random.Range(-spreadAngle, spreadAngle);
                float angleRad = angle * Mathf.Deg2Rad;

                // 거리도 약간 변동 (±10%)
                float dist = baseDist * UnityEngine.Random.Range(0.9f, 1.1f);

                Vector3 dir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));
                Vector3 pos = motherPos + dir * dist;
                pos.y = baseY;

                // 모선 바라봄
                Vector3 lookDir = motherPos - pos;
                lookDir.y = 0f;
                Quaternion rot = lookDir.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(lookDir, Vector3.up)
                    : Quaternion.identity;

                result.Add(new EnemySpawnData { position = pos, rotation = rot });
            }

            return result;
        }

        /// <summary>
        /// 파상 포메이션: 같은 방향에서 2~4 웨이브, 150~400m 간격
        /// Python: wave_attack — 여러 줄로 나누어 시차 공격
        /// </summary>
        public static List<EnemySpawnData> GenerateWave(
            Vector3 motherPos, int count, float baseDist, float baseY)
        {
            var result = new List<EnemySpawnData>();
            if (count <= 0) return result;

            float baseAngle = UnityEngine.Random.Range(0f, 360f);
            float baseAngleRad = baseAngle * Mathf.Deg2Rad;
            Vector3 approachDir = new Vector3(Mathf.Sin(baseAngleRad), 0f, Mathf.Cos(baseAngleRad));

            // 웨이브 수: 2~4 (적 수에 따라)
            int numWaves = Mathf.Clamp(count, 2, 4);
            if (count <= 2) numWaves = count;

            // 웨이브 간 거리: 150~400m
            float waveGap = UnityEngine.Random.Range(150f, 400f);

            // 각 웨이브에 균등 분배
            int perWave = Mathf.CeilToInt((float)count / numWaves);
            int placed = 0;

            for (int w = 0; w < numWaves && placed < count; w++)
            {
                float waveDist = baseDist + w * waveGap;

                // 같은 웨이브 내 소폭 좌우 분산 (±10°)
                int thisWaveCount = Mathf.Min(perWave, count - placed);
                for (int i = 0; i < thisWaveCount; i++)
                {
                    float lateralAngle = baseAngle + UnityEngine.Random.Range(-10f, 10f);
                    float rad = lateralAngle * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                    float dist = waveDist * UnityEngine.Random.Range(0.95f, 1.05f);
                    Vector3 pos = motherPos + dir * dist;
                    pos.y = baseY;

                    Vector3 lookDir = motherPos - pos;
                    lookDir.y = 0f;
                    Quaternion rot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;

                    result.Add(new EnemySpawnData { position = pos, rotation = rot });
                    placed++;
                }
            }

            return result;
        }

        /// <summary>
        /// 양동 포메이션: 3방향 (120° 간격) 각 방향 소규모 클러스터
        /// Python: diversionary_attack — 다방면 분산 공격
        /// </summary>
        public static List<EnemySpawnData> GenerateDiversionary(
            Vector3 motherPos, int count, float baseDist, float baseY)
        {
            var result = new List<EnemySpawnData>();
            if (count <= 0) return result;

            // 기본 각도 (랜덤)
            float baseAngle = UnityEngine.Random.Range(0f, 360f);
            // 3방향: 0°, 120°, 240°
            float[] directions = { 0f, 120f, 240f };

            // 각 방향에 분배
            int placed = 0;
            for (int d = 0; d < directions.Length && placed < count; d++)
            {
                float dirAngle = baseAngle + directions[d];

                // 이 방향에 배치할 수 (균등 분배, 나머지는 첫 방향에)
                int thisCount;
                if (d < directions.Length - 1)
                    thisCount = Mathf.Max(1, count / 3);
                else
                    thisCount = count - placed;
                thisCount = Mathf.Min(thisCount, count - placed);

                for (int i = 0; i < thisCount; i++)
                {
                    // 방향 내 소폭 분산 (±15°)
                    float angle = dirAngle + UnityEngine.Random.Range(-15f, 15f);
                    float angleRad = angle * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Sin(angleRad), 0f, Mathf.Cos(angleRad));

                    // 거리 변동 (±15%)
                    float dist = baseDist * UnityEngine.Random.Range(0.85f, 1.15f);
                    Vector3 pos = motherPos + dir * dist;
                    pos.y = baseY;

                    Vector3 lookDir = motherPos - pos;
                    lookDir.y = 0f;
                    Quaternion rot = lookDir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(lookDir, Vector3.up)
                        : Quaternion.identity;

                    result.Add(new EnemySpawnData { position = pos, rotation = rot });
                    placed++;
                }
            }

            return result;
        }
    }
}
