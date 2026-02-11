#!/usr/bin/env python3
"""
Real-time Visualization Dashboard
Unity 시뮬레이션 실시간 시각화
"""

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation
from unity_client import UnityClient
import threading
import time
from collections import deque

class Dashboard:
    """실시간 대시보드"""
    def __init__(self, host='localhost', port=9876, max_history=1000):
        self.client = UnityClient(host, port)
        self.max_history = max_history
        
        # 데이터 버퍼
        self.positions = deque(maxlen=max_history)
        self.velocities = deque(maxlen=max_history)
        self.rewards = deque(maxlen=max_history)
        self.throttles = deque(maxlen=max_history)
        self.steerings = deque(maxlen=max_history)
        self.radar_data = None
        
        # 현재 관측
        self.current_obs = None
        self.lock = threading.Lock()
        
        # 수신 스레드
        self.running = False
        self.receive_thread = None
        
    def connect(self):
        """Unity 연결"""
        if not self.client.connect():
            return False
        
        # 수신 스레드 시작
        self.running = True
        self.receive_thread = threading.Thread(target=self._receive_loop)
        self.receive_thread.daemon = True
        self.receive_thread.start()
        
        return True
    
    def _receive_loop(self):
        """백그라운드 데이터 수신"""
        while self.running:
            obs = self.client.receive_observation()
            if obs is not None:
                with self.lock:
                    self.current_obs = obs
                    self._update_data(obs)
            time.sleep(0.01)
    
    def _update_data(self, obs):
        """데이터 버퍼 업데이트"""
        # 위치
        pos = obs.get('position', [0, 0, 0])
        self.positions.append(pos)
        
        # 속도
        vel = obs.get('velocity', [0, 0, 0])
        speed = np.linalg.norm(vel)
        self.velocities.append(speed)
        
        # 레이더
        radar = obs.get('radarData', [1.0] * 8)
        self.radar_data = radar
        
        # 보상 (간단히 속도 기반)
        reward = speed * 0.1
        self.rewards.append(reward)
    
    def send_manual_action(self, throttle, steering):
        """수동 액션 전송"""
        self.client.send_action(throttle, steering)
        self.throttles.append(throttle)
        self.steerings.append(steering)
    
    def visualize(self, update_interval=100):
        """실시간 시각화"""
        fig = plt.figure(figsize=(14, 8))
        
        # 1. 2D 궤적
        ax1 = plt.subplot(2, 3, 1)
        line_trajectory, = ax1.plot([], [], 'b-', alpha=0.5, label='Trajectory')
        point_current, = ax1.plot([], [], 'ro', markersize=10, label='Current')
        ax1.set_xlabel('X Position (m)')
        ax1.set_ylabel('Z Position (m)')
        ax1.set_title('Ship Trajectory (Top View)')
        ax1.grid(True)
        ax1.legend()
        ax1.axis('equal')
        
        # 2. 속도 그래프
        ax2 = plt.subplot(2, 3, 2)
        line_velocity, = ax2.plot([], [], 'g-', label='Speed')
        ax2.set_xlabel('Time Step')
        ax2.set_ylabel('Speed (m/s)')
        ax2.set_title('Velocity Over Time')
        ax2.grid(True)
        ax2.legend()
        
        # 3. 레이더 (극좌표)
        ax3 = plt.subplot(2, 3, 3, projection='polar')
        angles = np.linspace(0, 2*np.pi, 9)
        line_radar, = ax3.plot(angles, [1]*9, 'r-o')
        ax3.set_ylim(0, 1)
        ax3.set_title('Radar View (8 directions)')
        
        # 4. 보상 누적
        ax4 = plt.subplot(2, 3, 4)
        line_reward, = ax4.plot([], [], 'm-', label='Cumulative Reward')
        ax4.set_xlabel('Time Step')
        ax4.set_ylabel('Reward')
        ax4.set_title('Reward Over Time')
        ax4.grid(True)
        ax4.legend()
        
        # 5. 액션 (Throttle)
        ax5 = plt.subplot(2, 3, 5)
        line_throttle, = ax5.plot([], [], 'b-', label='Throttle')
        line_steering, = ax5.plot([], [], 'r-', label='Steering')
        ax5.set_xlabel('Time Step')
        ax5.set_ylabel('Action')
        ax5.set_title('Control Actions')
        ax5.set_ylim(-1.1, 1.1)
        ax5.grid(True)
        ax5.legend()
        
        # 6. 상태 텍스트
        ax6 = plt.subplot(2, 3, 6)
        ax6.axis('off')
        text_state = ax6.text(0.1, 0.5, '', fontsize=10, verticalalignment='center',
                              family='monospace')
        
        def update(frame):
            """애니메이션 업데이트"""
            with self.lock:
                if len(self.positions) == 0:
                    return
                
                # 1. 궤적
                positions_np = np.array(self.positions)
                if len(positions_np) > 0:
                    line_trajectory.set_data(positions_np[:, 0], positions_np[:, 2])
                    point_current.set_data([positions_np[-1, 0]], [positions_np[-1, 2]])
                    
                    # 축 범위 자동 조정
                    x_min, x_max = positions_np[:, 0].min(), positions_np[:, 0].max()
                    z_min, z_max = positions_np[:, 2].min(), positions_np[:, 2].max()
                    margin = 10
                    ax1.set_xlim(x_min - margin, x_max + margin)
                    ax1.set_ylim(z_min - margin, z_max + margin)
                
                # 2. 속도
                if len(self.velocities) > 0:
                    line_velocity.set_data(range(len(self.velocities)), list(self.velocities))
                    ax2.set_xlim(0, len(self.velocities))
                    ax2.set_ylim(0, max(self.velocities) * 1.1 if max(self.velocities) > 0 else 10)
                
                # 3. 레이더
                if self.radar_data is not None:
                    radar_values = list(self.radar_data) + [self.radar_data[0]]
                    line_radar.set_ydata(radar_values)
                
                # 4. 보상
                if len(self.rewards) > 0:
                    cumulative = np.cumsum(self.rewards)
                    line_reward.set_data(range(len(cumulative)), cumulative)
                    ax4.set_xlim(0, len(cumulative))
                    ax4.set_ylim(min(cumulative) * 1.1, max(cumulative) * 1.1)
                
                # 5. 액션
                if len(self.throttles) > 0:
                    line_throttle.set_data(range(len(self.throttles)), list(self.throttles))
                if len(self.steerings) > 0:
                    line_steering.set_data(range(len(self.steerings)), list(self.steerings))
                max_len = max(len(self.throttles), len(self.steerings))
                ax5.set_xlim(0, max_len)
                
                # 6. 상태 텍스트
                if self.current_obs:
                    state_text = "=== Current State ===\n"
                    pos = self.current_obs.get('position', [0, 0, 0])
                    vel = self.current_obs.get('velocity', [0, 0, 0])
                    rot = self.current_obs.get('rotation', [0, 0, 0])
                    
                    state_text += f"Position: ({pos[0]:.2f}, {pos[1]:.2f}, {pos[2]:.2f})\n"
                    state_text += f"Velocity: ({vel[0]:.2f}, {vel[1]:.2f}, {vel[2]:.2f})\n"
                    state_text += f"Speed: {np.linalg.norm(vel):.2f} m/s\n"
                    state_text += f"Rotation: ({rot[0]:.1f}, {rot[1]:.1f}, {rot[2]:.1f})\n"
                    state_text += f"Health: {self.current_obs.get('health', 0):.1f}\n"
                    state_text += f"Ammo: {self.current_obs.get('ammo', 0):.1f}\n"
                    state_text += f"Steps: {self.current_obs.get('stepCount', 0)}\n"
                    
                    text_state.set_text(state_text)
        
        anim = FuncAnimation(fig, update, interval=update_interval, blit=False)
        plt.tight_layout()
        plt.show()
    
    def close(self):
        """종료"""
        self.running = False
        if self.receive_thread:
            self.receive_thread.join(timeout=1)
        self.client.disconnect()


def main():
    """대시보드 실행"""
    print("=== Unity Dashboard ===")
    print("Connecting to Unity...")
    
    dashboard = Dashboard(host='localhost', port=9876)
    
    if not dashboard.connect():
        print("Failed to connect!")
        return
    
    print("Connected! Starting visualization...")
    print("Press Ctrl+C to stop")
    
    try:
        # 시각화 시작 (블로킹)
        dashboard.visualize(update_interval=100)
    except KeyboardInterrupt:
        print("\nStopping...")
    finally:
        dashboard.close()


if __name__ == '__main__':
    main()
