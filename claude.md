# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요
- Unity 기반 해상 시뮬레이션 + ML-Agents 강화학습
- 목표: 복수 아군 USV 쌍이 차단망(Web)으로 다수 적군 선박 순차 포획
- MA-POCA (Multi-Agent POsthumous Credit Assignment) + CTDE 구조
- Gerstner 파도 + 풍력 외란 포함 사실적 해양 환경

## 핵심 파일 구조

### ML-Agents 핵심
| 파일 | 설명 |
|------|------|
| `Assets/Scripts/Defense/DefenseEnvController.cs` | 환경 컨트롤러: 에피소드 관리, 보상 분배, 종료 조건, FixedJoint convoy 로직 |
| `Assets/Scripts/Defense/DefenseRewardCalculator.cs` | 보상 계수 저장소 (Inspector 노출) |
| `Assets/Scripts/MLAgents/DefenseAgent.cs` | 에이전트: BufferSensor 관측, 액션 처리, LOS Guidance |
| `Assets/Scripts/Defense/EnemyFormationSpawner.cs` | 적군 포메이션 생성 (집중/파상/양동/랜덤) |
| `Assets/Scripts/Defense/LaunchZoneManager.cs` | 아군 진수구역 + 다중 쌍 풀 관리 |
| `Assets/Scripts/Defense/MLAgents/DynamicWeb.cs` | 두 선박 간 차단망 물리 |
| `config/defense_boat_trainer.yaml` | 학습 하이퍼파라미터 |

### 선박 물리
| 파일 | 설명 |
|------|------|
| `Assets/Scripts/Boat/Engine.cs` | 엔진 추진력 (ForceMode.Acceleration), Gerstner 파도 연동 |
| `Assets/Scripts/Boat/Boat.cs` | 선박 기본 로직 |

## 기동 방식: 쌍동선 → 단동선 전환 (FixedJoint 브랜치)

3단계 프로세스:
1. **Convoy** (쌍동선): FixedJoint로 두 선박 물리 연결 → 단일체로 접근
2. **Deploy** (그물 전개): 적 근처 도달 시 Joint 해제 → 그물 점진 전개
3. **Separated** (단동선): 그물 임계폭 도달 → 개별 기동으로 포획

## 관측 공간 (BufferSensor 기반)

VectorSensor(0) + 가변 BufferSensor 2개:
- **EnemyBufferSensor**: 활성 적군 N대 × 3 (dist, signedBearing, headingDiff) — 거리순 정렬
- **AllyBufferSensor**: 아군 쌍/Phantom/트랩 M개 × 3 (dist, bearing, webLength)

### 정규화 함수
| 대상 | 함수 | 범위 |
|------|------|------|
| 거리 | `(x-k)/(|x|+k)` | [-1, 1) — 주의: k에서 영점, 부호 전환 |
| 베어링 | `sqrt(angle/180) × sign(cross)` | [-1, 1] |
| 헤딩차 | `cos(δ/2) × sign(δ)` | [-1, 1] |

## 액션 공간

```csharp
float throttleInput = actions.ContinuousActions[0];  // -1 ~ 1
float steeringInput = actions.ContinuousActions[1];  // -1 ~ 1
// Throttle: (-1~1) → (0.5~1.0)
float throttle = (throttleInput + 1f) * 0.25f + 0.5f;
```

## Stage 시스템 (Curriculum Learning)

Stage1~Stage9까지 확장. 주요 Stage:
- **Stage1_Formation**: 대형 유지
- **Stage2_Capture**: 포획
- **Stage3_Tactical**: 종합
- **Stage6_PhantomFormation**: Phantom 가상 아군 쌍
- **Stage9_DisarmReform**: 포획 후 이탈 + 트랩 그물

## 에피소드 종료 조건

| 조건 | 트리거 |
|------|--------|
| `MaxEnvironmentSteps` | `_resetTimer >= maxEnvironmentSteps` (기본 2500) |
| `AllEnemiesNeutralized` | 모든 적 무력화 (정상 종료) |
| `NoPairsLeft` | 모든 아군 쌍 소진 (`disableNoPairsEndEpisode=false`일 때만) |
| `AllyHitWeb` | 아군이 타 쌍 그물에 충돌 (레거시) |
| `FriendlyCollision` | 아군끼리 충돌 (레거시) |

## 알려진 이슈

- **에피소드 시작 시 배 날아감**: transform 대신 `rb.position/rotation` 사용 + `rb.Sleep()`
- **time_scale 20 이상**: Gerstner 파도 불안정 → 10 이하 권장
- **멀티 환경**: `FindObjectOfType` → `GetComponentInChildren` 사용 필수
- **Water System**: Dictionary 중복 키 → `Cleanup()` 먼저 호출
- **AttackBoatDisabler**: `Destroy` 대신 `SetActive(false)` 사용 필수
- **Unity 씬 직접 편집**: 유니티 실행 중 외부에서 .unity 파일 수정해도 무시됨 → Inspector에서 변경 + Ctrl+S
- **`_prevThrottle` 초기값**: 0이면 Lerp로 throttle이 0.5 미만 → 0.5로 초기화 필요
- **거리 정규화 `(x-k)/(|x|+k)`**: k에서 영점 통과, 부호 전환으로 네트워크 혼란 가능 → `k/(x+k)` 방식 검토 중

## 학습 명령어

```bash
mlagents-learn config/defense_boat_trainer.yaml --run-id=defense_v1          # 새 학습
mlagents-learn config/defense_boat_trainer.yaml --run-id=defense_v1 --resume # 이어학습
tensorboard --logdir=results                                                  # 모니터링
```

## 자주 수정하는 파라미터

| 파라미터 | 파일 | 변수명 |
|----------|------|--------|
| 학습 스테이지 | DefenseEnvController Inspector | `currentStage` |
| 최대 환경 스텝 | DefenseEnvController | `maxEnvironmentSteps` |
| 보상 계수 전체 | DefenseRewardCalculator Inspector | 각종 coeff/penalty |
| 적군 돌진 속도 | DefenseEnvController Inspector | `enemyRushThrottle` |
| 포메이션 타입 | EnemyFormationSpawner Inspector | `formationType` |
| 활성 쌍 수 | LaunchZoneManager Inspector | `activePairCount` |
| 관측 정규화 K | DefenseAgent Inspector | `enemyNormK`, `allyPairNormK` |
