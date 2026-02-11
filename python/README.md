# Unity RL Pipeline for BoatAttack

**ML-Agents에서 독립된 커스텀 PyTorch 강화학습 파이프라인**

Unity 빌드 파일과 Python을 소켓 통신으로 연결하여:
- 실시간 데이터 수집
- PyTorch PPO 학습
- 실시간 시각화 대시보드
- 독립적인 데이터 분석

---

## 🏗️ 아키텍처

```
Unity Build (roonshot.x86_64)
    ↕ TCP Socket (port 9876)
Python RL Pipeline
    ├─ unity_client.py    - Unity 통신 클라이언트
    ├─ ppo_agent.py       - PyTorch PPO 에이전트
    ├─ train.py           - 학습 스크립트
    └─ dashboard.py       - 실시간 시각화
```

---

## 🚀 Quick Start

### 1. Unity 빌드 실행
```bash
cd /home/yune/BUILD
./roonshot.x86_64
```

### 2. Python 가상환경 활성화
```bash
cd /home/yune/BoatAttack
source rl_env/bin/activate
```

### 3-A. 학습 시작
```bash
cd python
python train.py
```

### 3-B. 실시간 대시보드 (시각화만)
```bash
cd python
python dashboard.py
```

### 3-C. 테스트 (랜덤 액션)
```bash
cd python
python unity_client.py
```

---

## 📂 파일 구조

```
BoatAttack/
├── Assets/
│   └── Scripts/
│       └── RL/
│           ├── SocketServer.cs    # Unity C# 소켓 서버
│           └── RLAgent.cs         # Unity RL 에이전트 래퍼
├── python/
│   ├── unity_client.py            # Unity 소켓 클라이언트
│   ├── ppo_agent.py               # PyTorch PPO 구현
│   ├── train.py                   # 학습 메인 스크립트
│   ├── dashboard.py               # 실시간 시각화
│   └── README.md                  # 이 파일
├── rl_env/                        # Python 가상환경
└── checkpoints/                   # 학습된 모델 저장
```

---

## 🎮 Unity 설정

### 1. SocketServer 추가
1. Unity Editor에서 빈 GameObject 생성 (이름: `RLManager`)
2. `SocketServer.cs` 컴포넌트 추가
3. Port: `9876` (기본값)
4. Auto Start: ✅

### 2. RLAgent 추가
1. 제어할 배(Ship) GameObject 선택
2. `RLAgent.cs` 컴포넌트 추가
3. Socket Server 참조 연결
4. Send Interval: `0.1` (10Hz)

### 3. 빌드
```
File → Build Settings
Platform: Linux
Build
```

---

## 🧠 학습 파라미터

### PPO 하이퍼파라미터 (ppo_agent.py)
```python
lr = 3e-4              # Learning rate
gamma = 0.99           # Discount factor
lambda_ = 0.95         # GAE lambda
epochs = 10            # PPO epochs per update
```

### 학습 설정 (train.py)
```python
num_episodes = 1000    # 총 에피소드 수
max_steps = 1000       # 에피소드당 최대 스텝
update_interval = 2048 # PPO 업데이트 주기
```

---

## 📊 데이터 저장

### 체크포인트
- 경로: `checkpoints/`
- 주기: 100 에피소드마다
- 파일명: `ppo_ep{episode}_{timestamp}.pt`

### 학습 곡선
- 경로: `checkpoints/training_curves.png`
- 내용: Reward, Episode Length, Loss

### 로그 추가 (선택)
```python
# train.py에 추가
import csv
with open('training_log.csv', 'a') as f:
    writer = csv.writer(f)
    writer.writerow([episode, reward, length, loss])
```

---

## 🎨 시각화 대시보드 (dashboard.py)

### 화면 구성
1. **2D 궤적 (Top View)** - 배의 이동 경로
2. **속도 그래프** - 시간에 따른 속도 변화
3. **레이더 뷰 (극좌표)** - 8방향 장애물 감지
4. **보상 누적** - 누적 보상 그래프
5. **액션** - Throttle, Steering 변화
6. **상태 텍스트** - 현재 위치, 속도, 회전 등

### 실행
```bash
python dashboard.py
```

---

## 🔧 트러블슈팅

### Unity 연결 실패
```
[UnityClient] Connection failed: [Errno 111] Connection refused
```
**해결:**
1. Unity 빌드가 실행 중인지 확인
2. `SocketServer.cs`가 GameObject에 추가되었는지 확인
3. Port 번호 확인 (9876)

### 관측 수신 없음
```
[UnityEnv] Warning: No observation received after reset
```
**해결:**
1. `RLAgent.cs`가 배에 추가되었는지 확인
2. `socketServer` 참조가 연결되었는지 확인
3. Unity Console에서 에러 확인

### 학습이 느림
**해결:**
1. `update_interval` 증가 (2048 → 4096)
2. `send_interval` 증가 (0.1 → 0.2)
3. GPU 사용 확인 (`torch.cuda.is_available()`)

---

## 🚢 LIG Nex1 스타일 확장

현재 구현은 **기본 파이프라인**입니다. 다음 단계:

### Phase 2: 고급 시각화
- [ ] Pygame 기반 대시보드 (더 빠름)
- [ ] 3D 시각화 (PyVista)
- [ ] 레이더 히트맵
- [ ] 다중 에이전트 뷰

### Phase 3: 고급 알고리즘
- [ ] SAC (Soft Actor-Critic)
- [ ] TD3 (Twin Delayed DDPG)
- [ ] Multi-Agent RL (MAPPO)

### Phase 4: 데이터 분석
- [ ] HDF5 대용량 저장
- [ ] Tensorboard 통합
- [ ] Jupyter Notebook 분석
- [ ] 실험 비교 도구

---

## 📖 참고 자료

- **Unity ML-Agents:** https://github.com/Unity-Technologies/ml-agents
- **PyTorch RL Tutorial:** https://pytorch.org/tutorials/intermediate/reinforcement_q_learning.html
- **PPO Paper:** https://arxiv.org/abs/1707.06347
- **LIG Nex1:** www.lignex1.com/main.do

---

## 🙋 FAQ

**Q: ML-Agents와 차이는?**  
A: ML-Agents는 Unity에 종속적. 이 파이프라인은 완전히 독립적으로 PyTorch 사용 가능.

**Q: 학습 속도는?**  
A: 10Hz 전송 시 초당 10 스텝. 1000 스텝 에피소드 = 100초 (~1.6분)

**Q: 다른 Unity 프로젝트에도 사용 가능?**  
A: 네! `SocketServer.cs`, `RLAgent.cs`만 추가하면 됨.

**Q: Windows/Mac에서도 동작?**  
A: 네! 소켓 통신은 플랫폼 독립적.

---

**🦾 Happy Training!**
