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

## 3. (연계) Fig.11 보상 스케일 점검 (리뷰 B-1)
- 누적보상 y축이 ≈2.4×10⁻⁵로 비정상 → 원인 후보: ① 연속보상 가중치(α_f 0.002·α_a 0.001·α_h 0.0003)가 작아 스텝당 미미 ② TensorBoard `Environment/Cumulative Reward` 축 라벨/스케일 ③ 지표 정의.
- 조치: 학습 로그(`results/<run>/`) TensorBoard 스칼라 재추출 → 누적보상 실제 범위 확인. 이벤트(±1) 포함 시 0.1~수 범위가 맞으면 **축 라벨 오류 정정**; 연속보상만 집계된 그래프면 지표 재정의.
- (도구) `EventAccumulator`로 `Environment/Cumulative Reward` 덤프 → 재플롯.

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
