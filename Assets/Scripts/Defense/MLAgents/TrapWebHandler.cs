using UnityEngine;
using Unity.MLAgents;

namespace BoatAttack
{
    /// <summary>
    /// Stage9: 포획 후 바다에 깔린 간이 트랩 그물
    /// 적군이 지나가면 포획 판정 (1회용, 정상 포획의 절반 보상)
    /// 아군이 지나가면 해당 쌍 비활성화 + 페널티
    /// </summary>
    public class TrapWebHandler : MonoBehaviour
    {
        [HideInInspector] public DefenseEnvController envController;
        [HideInInspector] public int remainingSteps;

        private const string ENEMY_TAG = "attack_boat";
        private int _lastAcademyStep;

        private void Start()
        {
            _lastAcademyStep = Academy.Instance.StepCount;
        }

        private void FixedUpdate()
        {
            // Academy step 기준으로 감소 (FixedUpdate 프레임이 아닌 실제 학습 스텝)
            int currentStep = Academy.Instance.StepCount;
            if (currentStep != _lastAcademyStep)
            {
                remainingSteps--;
                _lastAcademyStep = currentStep;
            }

            if (remainingSteps <= 0)
            {
                Destroy(gameObject);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (envController == null) return;

            // 적군 포획
            if (other.CompareTag(ENEMY_TAG))
            {
                envController.OnEnemyHitTrap(other.gameObject);
                Destroy(gameObject);
                return;
            }

            // 아군 충돌: DefenseAgent 컴포넌트로 식별
            var defenseAgent = other.GetComponent<DefenseAgent>();
            if (defenseAgent == null)
                defenseAgent = other.GetComponentInParent<DefenseAgent>();
            if (defenseAgent != null)
            {
                envController.OnAllyHitTrap(defenseAgent);
                // 트랩은 유지 (1회용 아님 — 아군 충돌로는 파괴하지 않음)
            }
        }
    }
}
