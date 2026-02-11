#!/usr/bin/env python3
"""
Unity Socket Client for RL Communication
Unity 빌드와 TCP 소켓으로 통신하는 클라이언트
"""

import socket
import json
import time
import numpy as np
from typing import Dict, Tuple, Optional

class UnityClient:
    def __init__(self, host='localhost', port=9876):
        self.host = host
        self.port = port
        self.socket = None
        self.buffer = ""
        
    def connect(self, timeout=10):
        """Unity 서버에 연결"""
        print(f"[UnityClient] Connecting to Unity at {self.host}:{self.port}...")
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.socket.settimeout(timeout)
        
        try:
            self.socket.connect((self.host, self.port))
            print("[UnityClient] Connected!")
            return True
        except Exception as e:
            print(f"[UnityClient] Connection failed: {e}")
            return False
    
    def disconnect(self):
        """연결 종료"""
        if self.socket:
            self.socket.close()
            print("[UnityClient] Disconnected")
    
    def send_action(self, throttle: float, steering: float, fire: bool = False):
        """액션을 Unity로 전송"""
        action = {
            'throttle': float(np.clip(throttle, 0.0, 1.0)),
            'steering': float(np.clip(steering, -1.0, 1.0)),
            'fire': bool(fire)
        }
        
        message = json.dumps(action) + "\n"
        try:
            self.socket.sendall(message.encode('utf-8'))
        except Exception as e:
            print(f"[UnityClient] Send error: {e}")
    
    def receive_observation(self) -> Optional[Dict]:
        """Unity로부터 관측 수신"""
        try:
            # 버퍼에 데이터 추가
            data = self.socket.recv(4096).decode('utf-8')
            if not data:
                return None
            
            self.buffer += data
            
            # 줄바꿈으로 메시지 분리
            if '\n' in self.buffer:
                lines = self.buffer.split('\n')
                self.buffer = lines[-1]  # 마지막 불완전한 라인 유지
                
                # 가장 최근 완전한 메시지 파싱
                for line in reversed(lines[:-1]):
                    if line.strip():
                        try:
                            obs = json.loads(line)
                            return obs
                        except json.JSONDecodeError:
                            continue
            
            return None
        except socket.timeout:
            return None
        except Exception as e:
            print(f"[UnityClient] Receive error: {e}")
            return None
    
    def reset(self):
        """에피소드 리셋 요청"""
        reset_msg = json.dumps({'command': 'reset'}) + "\n"
        try:
            self.socket.sendall(reset_msg.encode('utf-8'))
        except Exception as e:
            print(f"[UnityClient] Reset error: {e}")


class UnityEnv:
    """
    Gymnasium 스타일의 Unity 환경 래퍼
    """
    def __init__(self, host='localhost', port=9876):
        self.client = UnityClient(host, port)
        self.observation_space_dim = None
        self.action_space_dim = 2  # throttle, steering
        self.current_obs = None
        
    def connect(self):
        """Unity에 연결"""
        return self.client.connect()
    
    def reset(self) -> np.ndarray:
        """환경 리셋"""
        self.client.reset()
        time.sleep(0.5)  # Unity가 리셋할 시간
        
        # 첫 관측 받기
        obs = None
        for _ in range(10):  # 최대 1초 대기
            obs = self.client.receive_observation()
            if obs is not None:
                break
            time.sleep(0.1)
        
        if obs is None:
            print("[UnityEnv] Warning: No observation received after reset")
            return np.zeros(20)  # 기본값
        
        self.current_obs = obs
        return self._parse_observation(obs)
    
    def step(self, action: np.ndarray) -> Tuple[np.ndarray, float, bool, Dict]:
        """
        한 스텝 실행
        
        Args:
            action: [throttle, steering] shape=(2,)
        
        Returns:
            observation, reward, done, info
        """
        throttle = float(action[0])
        steering = float(action[1]) if len(action) > 1 else 0.0
        
        # 액션 전송
        self.client.send_action(throttle, steering, fire=False)
        
        # 관측 수신
        obs = self.client.receive_observation()
        if obs is None:
            # 타임아웃 - 이전 관측 재사용
            obs = self.current_obs
        
        self.current_obs = obs
        
        # 파싱
        state = self._parse_observation(obs)
        reward = self._calculate_reward(obs)
        done = obs.get('done', False)
        info = {'raw_obs': obs}
        
        return state, reward, done, info
    
    def _parse_observation(self, obs: Dict) -> np.ndarray:
        """관측 데이터를 numpy 배열로 변환"""
        if obs is None:
            return np.zeros(20)
        
        state = []
        
        # 위치 (3)
        state.extend(obs.get('position', [0, 0, 0]))
        
        # 속도 (3)
        state.extend(obs.get('velocity', [0, 0, 0]))
        
        # 회전 (3)
        state.extend(obs.get('rotation', [0, 0, 0]))
        
        # 레이더 (8)
        state.extend(obs.get('radarData', [1.0] * 8))
        
        # 상태 (2)
        state.append(obs.get('health', 100.0) / 100.0)  # 정규화
        state.append(obs.get('ammo', 50.0) / 100.0)
        
        # stepCount (1)
        state.append(obs.get('stepCount', 0) / 1000.0)  # 정규화
        
        return np.array(state, dtype=np.float32)
    
    def _calculate_reward(self, obs: Dict) -> float:
        """보상 계산 (예시)"""
        if obs is None:
            return 0.0
        
        reward = 0.0
        
        # 속도 보상 (앞으로 가도록)
        velocity = obs.get('velocity', [0, 0, 0])
        forward_speed = velocity[2]  # z축 속도
        reward += forward_speed * 0.1
        
        # 생존 보상
        if not obs.get('done', False):
            reward += 0.01
        
        # 체력 패널티
        health = obs.get('health', 100.0)
        if health < 50:
            reward -= 0.1
        
        return reward
    
    def close(self):
        """환경 종료"""
        self.client.disconnect()


def main():
    """테스트 코드"""
    print("=== Unity Client Test ===")
    
    env = UnityEnv(host='localhost', port=9876)
    
    if not env.connect():
        print("Failed to connect to Unity!")
        return
    
    try:
        # 리셋
        obs = env.reset()
        print(f"Initial observation shape: {obs.shape}")
        print(f"Observation: {obs}")
        
        # 몇 스텝 실행
        for step in range(100):
            # 랜덤 액션
            action = np.random.uniform([0.0, -1.0], [1.0, 1.0])
            
            obs, reward, done, info = env.step(action)
            
            print(f"Step {step}: Reward={reward:.3f}, Done={done}")
            
            if done:
                print("Episode done!")
                obs = env.reset()
            
            time.sleep(0.1)
    
    except KeyboardInterrupt:
        print("\nStopped by user")
    
    finally:
        env.close()


if __name__ == '__main__':
    main()
