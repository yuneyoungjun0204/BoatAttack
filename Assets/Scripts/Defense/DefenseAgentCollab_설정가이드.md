# DefenseAgentCollab 설정 가이드

## 1. DefenseSettings 설정

### 1-1. DefenseSettings GameObject 생성
1. Unity Hierarchy 창에서 **우클릭** → **Create Empty**
2. 이름을 `DefenseSettings`로 변경
3. Inspector에서 **Add Component** → `Defense Settings` (BoatAttack 네임스펙스) 추가

### 1-2. DefenseSettings 값 설정
- **Agent Run Speed**: 에이전트 이동 속도 (기본값: 10)
- **Agent Rotation Speed**: 회전 속도 (기본값: 200)
- **Spawn Area Margin Multiplier**: 스폰 영역 마진 (기본값: 0.9)

---

## 2. DefenseAgentCollab 설정

### 2-1. 방어 에이전트 GameObject 준비
방어 에이전트로 사용할 보트 GameObject가 필요합니다 (예: `DefenseAgent1`, `DefenseAgent2`)

### 2-2. DefenseAgentCollab 컴포넌트 추가
1. 방어 에이전트 GameObject 선택
2. Inspector에서 **Add Component** → `Defense Agent Collab` (BoatAttack 네임스펙스) 추가

### 2-3. 필수 컴포넌트 확인
다음 컴포넌트들이 있어야 합니다:
- ✅ **Boat** 컴포넌트 (자동으로 찾음)
- ✅ **Rigidbody** (Boat/Engine에 포함되어 있음)
- ✅ **Engine** (Boat 컴포넌트에서 자동으로 찾음)

### 2-4. Movement Settings 설정
- **Agent Run Speed**: DefenseSettings가 없을 때 사용할 속도 (기본값: 10)
- **Rotation Speed**: DefenseSettings가 없을 때 사용할 회전 속도 (기본값: 200)
- **Steering Sensitivity**: 조종 감도 (기본값: 1.0)

---

## 3. ML-Agents 필수 컴포넌트 추가

### 3-1. Behavior Parameters 추가
1. 방어 에이전트 GameObject 선택
2. Inspector에서 **Add Component** → `Behavior Parameters` (ML-Agents) 추가
3. 설정:
   - **Behavior Name**: `DefenseAgent` (또는 원하는 이름)
   - **Behavior Type**: `Default` (학습) 또는 `InferenceOnly` (추론)
   - **Vector Observation**:
     - **Space Size**: 관측값 개수 (예: 0, DefenseAgentCollab은 관측 없음)
   - **Actions**:
     - **Space Type**: `Continuous`
     - **Space Size**: `2` (throttle, steering)

### 3-2. Decision Requester 추가
1. 방어 에이전트 GameObject 선택
2. Inspector에서 **Add Component** → `Decision Requester` (ML-Agents) 추가
3. 설정:
   - **Decision Period**: `5` (5 프레임마다 결정)
   - **Take Actions Between Decisions**: ✅ 체크

---

## 4. DefenseEnvController 연결

### 4-1. DefenseEnvController 찾기
씬에 `DefenseEnvController` 컴포넌트가 있는 GameObject를 찾습니다.

### 4-2. 에이전트 할당
1. `DefenseEnvController` GameObject 선택
2. Inspector에서 `Defense Env Controller` 컴포넌트 찾기
3. **Agents** 섹션:
   - **Defense Agent 1**: 첫 번째 방어 에이전트 GameObject 드래그
   - **Defense Agent 2**: 두 번째 방어 에이전트 GameObject 드래그

⚠️ **주의**: `DefenseEnvController`는 `DefenseAgent` 타입을 기대하지만, `DefenseAgentCollab`을 사용하려면 `DefenseEnvController`도 수정이 필요할 수 있습니다.

---

## 5. SimpleMultiAgentGroup 설정 (선택사항)

### 5-1. SimpleMultiAgentGroup 추가
1. `DefenseEnvController` GameObject 선택
2. Inspector에서 **Add Component** → `Simple Multi Agent Group` (ML-Agents) 추가
3. `DefenseEnvController`의 **M Agent Group** 필드에 할당

### 5-2. 에이전트 등록
`DefenseEnvController`의 `Start()` 메서드에서 자동으로 등록됩니다.

---

## 6. 테스트 (Heuristic 모드)

### 6-1. Behavior Parameters 설정
1. 방어 에이전트 GameObject 선택
2. **Behavior Parameters** 컴포넌트에서:
   - **Behavior Type**: `Heuristic Only` 선택

### 6-2. 키보드 조작
- **W**: 전진
- **S**: 후진
- **A**: 좌회전
- **D**: 우회전

---

## 7. 학습 모드 설정

### 7-1. Behavior Parameters 설정
1. 방어 에이전트 GameObject 선택
2. **Behavior Parameters** 컴포넌트에서:
   - **Behavior Type**: `Default` 선택
   - **Model**: 학습된 모델 파일 (.onnx) 할당 (학습 후)

### 7-2. ML-Agents 학습 실행
터미널에서:
```bash
mlagents-learn config.yaml --run-id=defense_agent_collab
```

---

## 8. 문제 해결

### 문제: "Boat 컴포넌트를 찾을 수 없습니다"
- **해결**: GameObject에 `Boat` 컴포넌트가 있는지 확인

### 문제: "Rigidbody를 찾을 수 없습니다"
- **해결**: `Boat` 또는 `Engine` 컴포넌트에 Rigidbody가 연결되어 있는지 확인

### 문제: 액션이 작동하지 않음
- **해결**: 
  1. `Decision Requester` 컴포넌트가 추가되었는지 확인
  2. `Behavior Parameters`의 Action Space가 `Continuous`, Size가 `2`인지 확인

### 문제: DefenseEnvController에서 에이전트를 찾을 수 없음
- **해결**: `DefenseEnvController`가 `DefenseAgent` 타입을 기대하므로, `DefenseAgentCollab`을 사용하려면 `DefenseEnvController`의 타입을 변경하거나 둘 다 지원하도록 수정 필요

---

## 9. DefenseEnvController 수정 필요 사항

`DefenseEnvController`가 `DefenseAgent` 타입만 지원한다면, `DefenseAgentCollab`도 지원하도록 수정해야 합니다:

```csharp
// DefenseEnvController.cs 수정 예시
[Header("Agents")]
[Tooltip("방어 에이전트 1 (DefenseAgent 또는 DefenseAgentCollab)")]
public Agent defenseAgent1; // DefenseAgent → Agent로 변경

[Tooltip("방어 에이전트 2 (DefenseAgent 또는 DefenseAgentCollab)")]
public Agent defenseAgent2; // DefenseAgent → Agent로 변경
```

또는 두 타입을 모두 지원하도록 오버로드 메서드를 추가할 수 있습니다.
