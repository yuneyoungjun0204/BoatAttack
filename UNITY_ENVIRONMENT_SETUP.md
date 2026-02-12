# Unity 환경 제어 설정 가이드 (파도/바람)

## ✨ 새 기능

1. **파도 강도 조절** ⛵
2. **바람 세기/방향 제어** 💨
3. **Inspector에서 쉽게 조절**
4. **프리셋 지원** (Calm, Light, Moderate, Rough, Storm)
5. **동적 날씨 변화**

---

## 📋 Unity 설정 단계

### 1단계: EnvironmentController 추가

**Unity Editor에서:**

1. **GameObject 생성**
   - Hierarchy → 우클릭 → Create Empty
   - 이름: `EnvironmentManager`

2. **스크립트 추가**
   ```
   /home/yune/RoonShot_CLI/Unity_Scripts/EnvironmentController.cs
   ```
   - EnvironmentManager 선택
   - Inspector → Add Component → EnvironmentController

3. **Inspector 설정**

#### 파도 설정
```
Wave Strength: 1.0  (0~5, 기본 1.0)
  - 0.3: 잔잔함
  - 1.0: 보통
  - 2.5: 거침
  - 4.0+: 폭풍

Wave Speed: 1.0  (0.1~3, 기본 1.0)
  - 0.5: 느린 파도
  - 1.0: 보통
  - 2.0: 빠른 파도

Wave Direction: 0°  (0~360°)
  - 0: 북쪽
  - 90: 동쪽
  - 180: 남쪽
  - 270: 서쪽
```

#### 바람 설정
```
Wind Strength: 5.0 m/s  (0~30)
  - 0~5: 약한 바람
  - 5~15: 보통 바람
  - 15~25: 강한 바람
  - 25+: 폭풍

Wind Direction: 0°  (0~360°)
  - 0: 북쪽에서 불어옴
  - 90: 동쪽에서
  - 180: 남쪽에서
  - 270: 서쪽에서

Wind Turbulence: 0.3  (0~1)
  - 0: 일정한 바람
  - 0.5: 변화무쌍
  - 1.0: 매우 난류
```

#### 프리셋
```
Current Preset: Moderate
  - Calm: 잔잔한 날씨
  - Light: 약간의 파도/바람
  - Moderate: 보통 (기본)
  - Rough: 거친 바다
  - Storm: 폭풍
```

#### 랜덤/동적 날씨
```
Randomize On Start: ☐  (체크하면 시작 시 랜덤)
Dynamic Weather: ☐  (체크하면 실시간 변화)
Weather Change Interval: 60초  (동적 변화 주기)
```

---

### 2단계: 선박에 환경 영향 추가 (선택)

**각 선박에 대해:**

1. **선박 GameObject 선택**
   - 예: `TeamB_1`, `EnemyBoat1`, `MotherShip`

2. **스크립트 추가**
   ```
   /home/yune/RoonShot_CLI/Unity_Scripts/ShipEnvironmentEffects.cs
   ```
   - Inspector → Add Component → ShipEnvironmentEffects

3. **Inspector 설정**

#### 자동 연결
```
Environment Controller: (자동 탐색됨)
  - 비어있으면 자동으로 찾음
  - 수동 할당도 가능
```

#### 바람 영향
```
Apply Wind: ☑  (체크: 바람 영향 받음)
Wind Influence: 0.3  (0~2)
  - 0: 영향 없음
  - 0.3: 약간 영향
  - 1.0: 완전히 영향
  - 2.0: 과도한 영향

Lateral Area: 10 m²  (선박 크기에 따라)
  - 작은 보트: 5~10
  - 중형 선박: 10~30
  - 대형 선박: 30~100
```

#### 파도 영향
```
Apply Waves: ☑
Wave Influence: 1.0  (0~2)
Apply Wave Motion: ☑  (롤링/피칭)
Roll Strength: 1.0  (좌우 흔들림)
Pitch Strength: 0.5  (앞뒤 흔들림)
```

#### 디버그
```
Show Debug Info: ☐  (체크하면 콘솔에 정보 출력)
```

---

## 🎮 사용 방법

### 방법 1: Inspector에서 직접 조절

**실시간 조절 (Play 모드에서):**

1. Play 버튼 클릭
2. Hierarchy에서 `EnvironmentManager` 선택
3. Inspector에서 슬라이더 조절
   - Wave Strength 조절 → 파도 즉시 변화
   - Wind Strength 조절 → 바람 즉시 변화
   - Wind Direction 회전 → 바람 방향 변화

**Scene View에서 확인:**
- 파란색 화살표: 파도 방향
- 하늘색 화살표: 바람 방향

---

### 방법 2: 프리셋 사용

**프리셋 버튼 (Play 전 또는 Play 중):**

```
Current Preset: Calm
  → 모든 파라미터가 잔잔한 날씨로 변경

Current Preset: Storm
  → 폭풍 설정으로 변경
```

**프리셋 비교:**

| 프리셋 | Wave | Wind | 설명 |
|-------|------|------|------|
| **Calm** | 0.3 | 2 m/s | 호수처럼 잔잔 |
| **Light** | 0.8 | 5 m/s | 약간의 파도 |
| **Moderate** | 1.5 | 10 m/s | 보통 바다 (기본) |
| **Rough** | 2.5 | 15 m/s | 거친 바다 |
| **Storm** | 4.0 | 25 m/s | 폭풍우 |

---

### 방법 3: C# 코드에서 제어

**다른 스크립트에서:**

```csharp
// EnvironmentController 찾기
EnvironmentController env = FindObjectOfType<EnvironmentController>();

// 파도 설정
env.waveStrength = 2.0f;
env.waveSpeed = 1.5f;
env.waveDirection = 90f;

// 바람 설정
env.windStrength = 15f;
env.windDirection = 180f;
env.windTurbulence = 0.5f;

// 적용
env.ApplySettings();

// 또는 프리셋 사용
env.ApplyPreset(EnvironmentController.WeatherPreset.Storm);

// 랜덤 날씨
env.RandomizeWeather();

// 현재 바람 벡터 가져오기
Vector3 wind = env.GetWindVector();
```

---

### 방법 4: Python에서 제어 (고급)

**추후 구현 가능:**

Unity C#에서 소켓으로 파도/바람 명령 수신:

```python
# Python
client.send_environment({
    "wave_strength": 2.5,
    "wave_speed": 1.2,
    "wind_strength": 15.0,
    "wind_direction": 90.0
})
```

---

## 🧪 테스트 시나리오

### 시나리오 1: 잔잔한 호수 → 폭풍 바다

1. Play 시작
2. `Current Preset: Calm` 선택
3. 30초 관찰 (선박이 거의 안 흔들림)
4. `Current Preset: Storm` 선택
5. 선박이 크게 흔들리는 것 확인

---

### 시나리오 2: 일방향 강풍 테스트

1. `Wind Strength: 20.0`
2. `Wind Direction: 90°` (동풍)
3. 모든 선박이 서쪽으로 밀리는지 확인

---

### 시나리오 3: 동적 날씨

1. `Dynamic Weather: ☑`
2. `Weather Change Interval: 30` (30초마다 변화)
3. Play 시작
4. 30초마다 파도/바람이 자동으로 변하는 것 관찰

---

### 시나리오 4: 선박별 다른 영향

**큰 모선:**
```
Wind Influence: 0.1  (바람에 덜 밀림)
Lateral Area: 50     (큰 면적)
Roll Strength: 0.3   (안정적)
```

**작은 공격정:**
```
Wind Influence: 0.8  (바람에 많이 밀림)
Lateral Area: 8      (작은 면적)
Roll Strength: 2.0   (많이 흔들림)
```

→ 같은 날씨에서도 선박마다 다르게 반응!

---

## 📊 RL 학습에 활용

### Hillstate4 환경 설정

**난이도별 설정:**

#### Easy (학습 초기)
```
Preset: Calm
Wave: 0.3
Wind: 2 m/s
→ 선박 제어가 쉬움
```

#### Medium (중간 단계)
```
Preset: Moderate
Wave: 1.5
Wind: 10 m/s
→ 약간의 외란
```

#### Hard (최종 테스트)
```
Preset: Rough
Wave: 2.5
Wind: 15 m/s
→ 실전 환경
```

#### Expert (극한 테스트)
```
Preset: Storm
Wave: 4.0
Wind: 25 m/s
Dynamic Weather: ☑
→ 변화무쌍한 환경
```

---

### Python 학습 스크립트 수정

**DefenseEnv에 환경 난이도 추가:**

```python
class DefenseEnv:
    def __init__(self, ..., difficulty='medium'):
        self.difficulty = difficulty
    
    def reset(self):
        # Unity에 환경 설정 전송 (추후 구현)
        if self.difficulty == 'easy':
            # Calm
            pass
        elif self.difficulty == 'hard':
            # Storm
            pass
        
        return obs, info
```

**Optuna에서 난이도 튜닝:**

```python
difficulty = trial.suggest_categorical('difficulty', ['easy', 'medium', 'hard'])
env = DefenseEnv(difficulty=difficulty)
```

---

## 🔧 트러블슈팅

### 파도가 안 보임

**원인:**
- Water 시스템이 없거나 비활성화됨

**해결:**
1. Hierarchy에서 Water GameObject 확인
2. Water 컴포넌트 활성화
3. EnvironmentController가 Water를 찾지 못하면 콘솔 경고 확인

---

### 바람 영향이 안 느껴짐

**원인:**
- `Wind Influence: 0.0` 또는
- `Apply Wind: ☐` 체크 해제

**해결:**
1. 각 선박의 `ShipEnvironmentEffects` 확인
2. `Apply Wind: ☑`
3. `Wind Influence: 0.5` 이상
4. EnvironmentManager의 `Wind Strength: 15+`

---

### 선박이 너무 많이 흔들림

**해결:**
1. `Roll Strength: 0.5` 이하로
2. `Pitch Strength: 0.3` 이하로
3. `Wave Influence: 0.5` 이하로

---

### Scene View에서 화살표 안 보임

**해결:**
- Play 모드여야 Gizmo가 그려짐
- Scene View → Gizmos 버튼 켜기

---

## 📝 요약

### 필수 설정

1. **EnvironmentManager GameObject 생성**
2. **EnvironmentController.cs 추가**
3. **Inspector에서 파도/바람 조절**
4. **프리셋으로 빠른 설정**

### 선택 설정

5. **각 선박에 ShipEnvironmentEffects.cs 추가**
6. **선박별 영향 계수 조절**

### 실행

- Play 버튼 → 실시간 조절 가능
- 프리셋으로 빠른 전환
- 동적 날씨로 자동 변화

---

**이제 Hillstate4에서 다양한 날씨 조건으로 학습할 수 있습니다!** ⛈️⛵💨
