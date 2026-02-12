# 환경 제어 시스템 설치 가이드 (5분)

## 📂 파일 위치

- ✅ **EnvironmentController.cs** → `Assets/Scripts/Environment/`
- ✅ **ShipEnvironmentEffects.cs** → `Assets/Scripts/Boat/`

---

## 🎮 Unity 설정 방법

### 1단계: EnvironmentController 추가

1. **Hierarchy** → 우클릭 → **Create Empty** → 이름: `EnvironmentManager`
2. `EnvironmentManager` 선택
3. **Inspector** → **Add Component** → 검색: `EnvironmentController`
4. **초기 설정** (Inspector):
   ```
   Current Preset: Moderate (보통)
   
   [Wave Settings]
   - Wave Strength: 1.0
   - Wave Speed: 1.0
   - Wave Direction: 0°
   
   [Wind Settings]
   - Wind Strength: 10.0 m/s
   - Wind Direction: 0°
   - Wind Turbulence: 0.3
   
   [Options]
   - Randomize On Start: ❌ (체크 안 함)
   - Dynamic Weather: ❌ (체크 안 함)
   ```

---

### 2단계: 선박에 환경 영향 추가 (선택적)

각 선박에 추가하면 파도/바람 영향을 받아요!

1. **Hierarchy**에서 선박 선택 (예: `TeamB_1`, `EnemyBoat1`)
2. **Inspector** → **Add Component** → 검색: `ShipEnvironmentEffects`
3. **자동 설정됨**:
   - Environment Controller 자동 탐지
   - 기본 영향 계수 적용
4. **세부 조정** (선택적):
   ```
   Wind Influence: 1.0 (바람 영향도)
   Wave Influence: 1.0 (파도 영향도)
   Roll Strength: 2.0 (좌우 기울기)
   Pitch Strength: 1.5 (앞뒤 기울기)
   ```

---

### 3단계: Play 후 실시간 조절

1. **Play 버튼** 클릭 ▶️
2. **Hierarchy** → `EnvironmentManager` 선택
3. **Inspector**에서 슬라이더 조절 → **즉시 반영!**

---

## 🌊 프리셋 사용법

Inspector에서 **Current Preset** 변경:

| 프리셋 | Wave Strength | Wind Strength | 난이도 |
|--------|---------------|---------------|--------|
| **Calm** | 0.3 | 2 m/s | 쉬움 ⭐ |
| **Light** | 0.8 | 5 m/s | 쉬움 ⭐⭐ |
| **Moderate** | 1.5 | 10 m/s | 보통 ⭐⭐⭐ |
| **Rough** | 2.5 | 15 m/s | 어려움 ⭐⭐⭐⭐ |
| **Storm** | 4.0 | 25 m/s | 매우 어려움 ⭐⭐⭐⭐⭐ |

프리셋 선택 후 **Apply Preset** 버튼 클릭!

---

## ⚡ 빠른 테스트

1. Unity Editor 열기
2. **ROONSHOOT** 씬 열기
3. **Tools** → **RoonShot** → **Auto Setup Everything** (이미 했으면 스킵)
4. **Hierarchy** → 우클릭 → **Create Empty** → `EnvironmentManager`
5. `EnvironmentManager`에 **EnvironmentController** 컴포넌트 추가
6. **Play** ▶️
7. **Storm** 프리셋 선택 → **Apply Preset** 버튼!

---

## 🔧 문제 해결

**Q: EnvironmentController가 안 보여요!**
- Unity Editor를 껐다 켜보세요 (스크립트 컴파일)
- `Assets/Scripts/Environment/EnvironmentController.cs` 파일 있는지 확인

**Q: ShipEnvironmentEffects가 작동 안 해요!**
- EnvironmentManager가 씬에 있는지 확인
- EnvironmentController 컴포넌트가 추가되어 있는지 확인

**Q: 파도/바람이 너무 세요!**
- Inspector에서 Strength 슬라이더를 낮춰보세요
- Calm 프리셋 사용해보세요

---

## 📝 다음 단계

환경 설정 완료 후:

1. **Python 대시보드 실행**:
   ```bash
   cd /home/yune/RoonShot_CLI
   ./run_tactical_display.sh --scale 1000
   ```

2. **강화학습 훈련**:
   ```bash
   ./scripts/optuna_realistic.sh
   ```

3. **다양한 날씨 조건 테스트**!

---

형, 바로 테스트 가능해요! 🚀
