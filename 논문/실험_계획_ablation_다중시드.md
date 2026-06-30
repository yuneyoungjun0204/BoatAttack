# 실험 계획 — MAPPO Ablation & 다중 시드 통계 (리뷰 B-2 / J 대응)

> 목적: "MA-POCA 채택 근거를 정량 입증"(ablation 부재 지적) + "단일 시드 40회 → 다중 시드·통계 변동성".
> 대상 브랜치: `two-USV-system` (논문 실험본). 학습: ML-Agents (`mlagents-learn`).

---

## 1. MAPPO Ablation
### 비교군 정의
| 조건 | trainer_type | 핵심 차이 |
|------|--------------|-----------|
| **MA-POCA** (제안) | `poca` | 사후 크레딧 할당 + counterfactual 중앙 critic (에이전트 이탈 대응) |
| **PPO/MAPPO** (ablation) | `ppo` | 공유정책 PPO, 사후 크레딧 없음 (이탈 시 크레딧 평가 불가) |
| **Modified LOS** (baseline) | — | 규칙기반 (기존) |

- ML-Agents에는 MA-POCA=`poca`, 독립/공유 PPO=`ppo`가 내장. **동일 관측·행동·보상·네트워크·하이퍼파라미터**로 `trainer_type`만 바꿔 공정 비교.
- 설정: `config/defense_boat_trainer.yaml` 복제 → `trainer_type: poca`(A) / `ppo`(B). 나머지(batch 1024, buffer 10240, lr 3e-4, β 0.005, ε 0.2, λ 0.95, epoch 3, γ 0.99, hidden 256×2, memory 128, seq 64) 동일.
- **검증 포인트(why MA-POCA)**: 포획 완료/충돌/대형붕괴로 **에이전트가 중도 비활성화**되는 빈도가 높은 상황에서, MA-POCA가 PPO 대비 (a) 포획률↑ (b) 학습 안정성(분산↓) (c) 대형 붕괴율↓ 를 보이는지.

### 절차
1. A(poca)·B(ppo) 각각 동일 `max_steps`(예 3M)로 학습.
2. 학습 곡선(보상/엔트로피/손실) 비교 → 안정성.
3. 아래 평가 프로토콜로 정량 비교.

---

## 2. 다중 시드 통계
### 시드 설계
- 각 조건(MA-POCA / PPO / LOS) × **시드 5개**(예: 1,2,3,4,5). LOS는 학습 없음 → 평가 시드만.
- 학습: `mlagents-learn config/xxx.yaml --run-id=poca_s1 --seed 1` … `--seed 5` (PPO도 동일).
- ML-Agents `--seed`가 환경·정책 초기화 시드를 고정.

### 평가 프로토콜
- 각 학습 모델(추론 모드)로 **고정 40 에피소드** 실행, 동일 적 포메이션 분포·동일 평가 시드.
- 지표: Capture rate, Penetration rate, Formation collapse rate (논문 정의 동일).
- 시드별 지표 → **평균 ± 표준편차** 보고. MA-POCA vs (PPO, LOS) **유의성 검정**(독립표본 t-test 또는 Mann–Whitney, n=5).
- 결과표 형식: 각 셀 `mean ± std`, 유의성 표기(*p<0.05 등).

### 산출
- 표: 조건×지표 (mean±std).
- 그림: 지표별 막대그래프(에러바=std) 또는 박스플롯.
- 본문: "MA-POCA가 PPO 대비 포획률 X±x%p, 붕괴율 Y±y%p로 통계적으로 유의(p<0.05)하게 우수" 식 서술.

---

## 3. (연계) Fig.11 보상 스케일 점검 (리뷰 K) — ✅ 완료
- **결과**: `results/0422_two/Defence` TensorBoard 덤프 → `Environment/Cumulative Reward`는 **3 → 12~15 수렴(raw 2.5~25)** 의 정상 범위. **2.4×10⁻⁵ 아님.**
- 2.4×10⁻⁵에 근접한 건 후반부 **Policy/Learning Rate**(2.99e-4→1.78e-4, 선형감쇠로 후반 ~2.4e-5대). → 원본 Fig.11은 **학습률을 누적보상으로 잘못 라벨/축 스케일 오류**로 결론.
- **산출**: 올바른 곡선 재생성 → `논문/figures/fig11_reward_corrected.png` (run=0422_two). 논문 Fig.11 교체 + 본문 "~1.1M 수렴" 유지(데이터상 0.5~1M부터 plateau).
- ⚠ 확인필요: `0422_two`가 논문 최종 run인지(65% 결과 산출 run) 사용자 확인 후 확정 교체.
- 다른 후보 run: `0421_two`(2.2~17), `0421_two_2`(0.25~15.6), `0418_two`(Group 6.25). 모두 O(1~25)로 스케일 결론 동일.

---

## 4. 실행 명령 (요약)
```bash
# MA-POCA 5시드
for s in 1 2 3 4 5; do
  mlagents-learn config/poca_defense.yaml --run-id=poca_s$s --seed $s
done
# PPO(ablation) 5시드
for s in 1 2 3 4 5; do
  mlagents-learn config/ppo_defense.yaml  --run-id=ppo_s$s  --seed $s
done
# 평가: 각 .onnx 모델 추론으로 40에피 × 시드 → 지표 집계 스크립트
```
- `time_scale 10`, `num_envs`/씬 복제로 가속.

## 5. 비용·우선순위
- 학습 run = 2조건 × 5시드 = **10회** (+ 기존 1회). 1 run 수 시간 → GPU 1장 기준 수일. (별도 GPU면 병렬.)
- 평가는 가벼움. **우선순위**: ① Fig.11 스케일 점검(가벼움) → ② MA-POCA vs PPO 단일시드 ablation(중간) → ③ 5시드 통계(무거움).

## 6. 산출물 반영
- 논문 §학습결과: ablation 표 + 다중시드 mean±std 표/그림 추가, "향후 과제" 문구 → 실제 결과로 대체.
- `실험_계획_ablation_다중시드.md`(본 문서) 갱신.
