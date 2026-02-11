# Unity Editor 설정 가이드

## 1. Unity Editor 열기
```bash
/home/yune/Unity/Hub/Editor/2022.3.62f3/Editor/Unity -projectpath /home/yune/BoatAttack
```

## 2. SocketServer 추가

### GameObject 생성
1. Hierarchy 창에서 우클릭
2. `Create Empty` 선택
3. 이름을 `RLManager`로 변경

### SocketServer 컴포넌트 추가
1. `RLManager` 선택
2. Inspector 창에서 `Add Component` 클릭
3. `Socket Server` 검색 및 추가
4. 설정:
   - Port: `9876`
   - Auto Start: ✅ (체크)

## 3. RLAgent 추가

### 배에 컴포넌트 추가
1. Hierarchy에서 제어할 배(Ship) 선택
   - 예: `BoatPrefab`, `PlayerBoat`, `국군` 등
2. Inspector에서 `Add Component` 클릭
3. `RL Agent` 검색 및 추가
4. 설정:
   - Socket Server: `RLManager` 드래그
   - Send Interval: `0.1` (10Hz)

## 4. Scene 저장
- `Ctrl+S` 또는 `File → Save`

## 5. 빌드 (선택)
새로 빌드하려면:
```
File → Build Settings
Platform: Linux
Build
```

저장 위치: `/home/yune/BUILD/roonshot.x86_64`

---

## 테스트 방법

### 방법 1: Unity Editor에서 Play
1. Unity Editor에서 Play 버튼 클릭
2. 터미널에서 Python 클라이언트 실행:
   ```bash
   cd /home/yune/BoatAttack/python
   source ../rl_env/bin/activate
   python unity_client.py
   ```

### 방법 2: 빌드 파일 실행
1. 빌드 실행:
   ```bash
   /home/yune/BUILD/roonshot.x86_64
   ```
2. Python 클라이언트 실행 (위와 동일)

---

## 주의사항

- Unity Console에서 `[SocketServer] Started on port 9876` 메시지 확인
- Python에서 `[UnityClient] Connected!` 메시지 확인
- 방화벽이 Port 9876을 막고 있지 않은지 확인
