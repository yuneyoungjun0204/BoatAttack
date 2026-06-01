using UnityEngine;

namespace BoatAttack
{
    public class DefenseRewardCalculator : MonoBehaviour
    {
        [Header("=== One-Way Towing 보상 ===")]
        [Tooltip("그물 전개 중(isSplitting) anchorDist 1m 증가당 보상. 권장: 0.002")]
        public float webGrowthRewardCoeff = 0.002f;

        [Tooltip("앵커드롭 시점 즉각 보상 계수. 그물 스윕방향 ⊥ 적 진로일수록 최대(1.0). 권장: 0.3~0.6")]
        public float anchorDropQualityReward = 0.4f;

        [Header("=== LOS 정렬 보상 ===")]
        [Tooltip("RL 조향이 LOS 베이스라인에 가까울수록 보상. 권장: 0.001")]
        public float losAlignmentRewardCoeff = 0.001f;

        [Tooltip("RL 속도가 LOS 선회감속 베이스라인에 가까울수록 보상. 권장: 0.001")]
        public float losThrottleAlignmentCoeff = 0.001f;

        [Header("=== 이벤트 보상 ===")]
        [Tooltip("포획 성공 보상")]
        public float captureReward = 1.0f;

        [Tooltip("포획한 쌍에 추가 개별 보너스")]
        public float capturePairBonus = 0.3f;

        [Tooltip("모선 충돌 페널티")]
        public float motherShipHitPenalty = -1.0f;

        [Tooltip("충돌 페널티 (아군끼리 등)")]
        public float collisionPenalty = -0.5f;

        [Header("=== Phase1 트랩 설치 보상 ===")]
        [Tooltip("1쌍 트랩 설치 완료 시 보상")]
        public float trapDeployBonus = 1.0f;

        [Tooltip("모든 쌍 트랩 설치 완료 보너스")]
        public float allTrapsDeployedBonus = 2.0f;

        [Header("=== 에피소드 종료 보상 ===")]
        public float allClearBonus = 2.0f;
        public float enemyBreachPenalty = -1.0f;
        public float remainingEnemyPenalty = -0.5f;
        public float noPairsLeftPenalty = -5f;

        private readonly System.Collections.Generic.Dictionary<int, float> _prevAnchorDist
            = new System.Collections.Generic.Dictionary<int, float>();

        public float CalculateWebGrowthReward(int pairIdx, float anchorDist)
        {
            if (webGrowthRewardCoeff <= 0f) return 0f;
            if (!_prevAnchorDist.TryGetValue(pairIdx, out float prevDist))
            {
                _prevAnchorDist[pairIdx] = anchorDist;
                return 0f;
            }
            float delta = anchorDist - prevDist;
            _prevAnchorDist[pairIdx] = anchorDist;
            return delta > 0f ? webGrowthRewardCoeff * delta : 0f;
        }

        public void Reset() => _prevAnchorDist.Clear();
    }
}
