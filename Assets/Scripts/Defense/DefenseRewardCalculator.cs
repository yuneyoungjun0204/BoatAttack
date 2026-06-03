using UnityEngine;

namespace BoatAttack
{
    public class DefenseRewardCalculator : MonoBehaviour
    {
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

        [Header("=== 에피소드 종료 보상 ===")]
        public float allClearBonus = 2.0f;
        public float enemyBreachPenalty = -1.0f;
        public float remainingEnemyPenalty = -0.5f;
        public float noPairsLeftPenalty = -5f;
    }
}
