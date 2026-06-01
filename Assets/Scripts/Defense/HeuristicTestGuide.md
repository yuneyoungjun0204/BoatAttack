# 휴리스틱 테스트 가이드

## 문제: 휴리스틱으로 조정이 안 먹어 움직이지 않음

### 해결 방법

#### 1. DecisionRequester 컴포넌트 추가 (필수)
각 DefenseAgent GameObject에 다음을 확인하세요:

1. **DefenseAgent GameObject 선택**
2. **Add Component** 클릭
3. **Decision Requester** 검색 및 추가
4. **설정**:
   - **Decision Period**: 5 (또는 원하는 값)
   - **Take Actions Between Decisions**: 체크 해제 (권장)
   - **Behavior Type**: **Heuristic Only** (테스트 시)

#### 2. Behavior Type 확인
- **테스트 시**: `Heuristic Only`로 설정
- **학습 시**: `Default` 또는 Behavior Config에서 설정

#### 3. 키보드 입력 확인
- **Agent 1**: WASD 키 사용
  - W: 전진
  - S: 후진
  - A: 좌회전
  - D: 우회전
- **Agent 2**: 화살표 키 사용
  - ↑: 전진
  - ↓: 후진
  - ←: 좌회전
  - →: 우회전

#### 4. 에이전트 이름 확인
DefenseAgent의 Heuristic 메서드는 GameObject 이름으로 에이전트를 구분합니다:
- Agent 1: 이름에 "1", "agent1", "defense1" 포함
- Agent 2: 이름에 "2", "agent2", "defense2" 포함

이름이 다르면 둘 다 같은 키(WASD 또는 화살표)를 사용합니다.

#### 5. Engine 컴포넌트 확인
- DefenseAgent가 `Boat` 컴포넌트를 가지고 있는지 확인
- `Boat.engine`이 null이 아닌지 확인
- `Engine.RB` (Rigidbody)가 null이 아닌지 확인

#### 6. 디버그 로그 확인
DefenseAgent의 `enableDebugLog`를 활성화하여 다음을 확인:
- 에피소드가 시작되는지
- OnActionReceived가 호출되는지
- _engine이 null인지

---

## 빠른 체크리스트

- [ ] DecisionRequester 컴포넌트 추가됨
- [ ] Behavior Type이 Heuristic Only로 설정됨
- [ ] Decision Period가 적절함 (5 권장)
- [ ] DefenseAgent 이름이 올바름 (1 또는 2 포함)
- [ ] Boat 컴포넌트가 있음
- [ ] Engine 컴포넌트가 있음
- [ ] Rigidbody가 있음
- [ ] 키보드 입력이 작동함 (다른 곳에서 테스트)

---

## 추가 디버깅

### OnActionReceived가 호출되는지 확인
DefenseAgent.cs에 다음을 추가:

```csharp
public override void OnActionReceived(ActionBuffers actions)
{
    Debug.Log($"[DefenseAgent] OnActionReceived 호출됨: {gameObject.name}");
    
    if (_engine == null || _engine.RB == null || _episodeEnded)
    {
        Debug.LogWarning($"[DefenseAgent] _engine 또는 RB가 null입니다!");
        return;
    }
    
    // ... 나머지 코드
}
```

### Heuristic이 호출되는지 확인
DefenseAgent.cs의 Heuristic 메서드에 다음을 추가:

```csharp
public override void Heuristic(in ActionBuffers actionsOut)
{
    Debug.Log($"[DefenseAgent] Heuristic 호출됨: {gameObject.name}");
    
    // ... 나머지 코드
}
```

---

## 일반적인 문제와 해결책

### 문제 1: 아무것도 움직이지 않음
- **원인**: DecisionRequester가 없음
- **해결**: DecisionRequester 추가

### 문제 2: 키를 눌러도 반응 없음
- **원인**: Behavior Type이 Heuristic Only가 아님
- **해결**: DecisionRequester의 Behavior Type을 Heuristic Only로 변경

### 문제 3: 한 에이전트만 움직임
- **원인**: 에이전트 이름이 올바르지 않음
- **해결**: 에이전트 이름에 "1" 또는 "2" 포함 확인

### 문제 4: 움직이지만 매우 느림
- **원인**: Decision Period가 너무 큼
- **해결**: Decision Period를 1~5로 낮춤

### 문제 5: 움직이지만 부자연스러움
- **원인**: 가속도 제한이 너무 엄격함
- **해결**: maxLinearAcceleration, maxAngularAcceleration 값 증가
