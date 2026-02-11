#!/usr/bin/env python3
"""
Unity RL Training Script
Unity 빌드와 연결하여 강화학습 학습
"""

import os
import time
import numpy as np
import matplotlib.pyplot as plt
from datetime import datetime
from unity_client import UnityEnv
from ppo_agent import PPOAgent

class Trainer:
    """학습 관리 클래스"""
    def __init__(self, save_dir='checkpoints'):
        self.save_dir = save_dir
        os.makedirs(save_dir, exist_ok=True)
        
        # 환경
        self.env = UnityEnv(host='localhost', port=9876)
        
        # 에이전트
        obs_dim = 20  # position(3) + velocity(3) + rotation(3) + radar(8) + health(1) + ammo(1) + step(1)
        action_dim = 2  # throttle, steering
        self.agent = PPOAgent(obs_dim, action_dim, lr=3e-4)
        
        # 학습 기록
        self.episode_rewards = []
        self.episode_lengths = []
        self.losses = []
        
    def train(self, num_episodes=1000, max_steps=1000, update_interval=2048):
        """학습 루프"""
        print("=== Training Start ===")
        print(f"Episodes: {num_episodes}, Max Steps: {max_steps}")
        print(f"Update Interval: {update_interval} steps")
        
        # Unity 연결
        if not self.env.connect():
            print("Failed to connect to Unity!")
            return
        
        global_step = 0
        
        try:
            for episode in range(num_episodes):
                obs = self.env.reset()
                episode_reward = 0
                episode_length = 0
                
                for step in range(max_steps):
                    # 액션 선택
                    action = self.agent.select_action(obs)
                    
                    # 환경 스텝
                    next_obs, reward, done, info = self.env.step(action)
                    
                    # 저장
                    self.agent.store_transition(reward, done)
                    
                    episode_reward += reward
                    episode_length += 1
                    global_step += 1
                    
                    obs = next_obs
                    
                    # PPO 업데이트
                    if global_step % update_interval == 0:
                        loss_info = self.agent.update(epochs=10)
                        self.losses.append(loss_info['loss'])
                        print(f"  [Update] Step {global_step}, Loss: {loss_info['loss']:.4f}")
                    
                    if done:
                        break
                
                # 에피소드 종료
                self.episode_rewards.append(episode_reward)
                self.episode_lengths.append(episode_length)
                
                # 로그
                avg_reward = np.mean(self.episode_rewards[-100:])
                print(f"Episode {episode:4d} | Steps: {episode_length:4d} | "
                      f"Reward: {episode_reward:7.2f} | Avg(100): {avg_reward:7.2f}")
                
                # 저장
                if (episode + 1) % 100 == 0:
                    self.save_checkpoint(episode)
                    self.plot_training()
        
        except KeyboardInterrupt:
            print("\n[Interrupted] Saving before exit...")
            self.save_checkpoint('final')
            self.plot_training()
        
        finally:
            self.env.close()
    
    def save_checkpoint(self, episode):
        """체크포인트 저장"""
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        filename = f"ppo_ep{episode}_{timestamp}.pt"
        path = os.path.join(self.save_dir, filename)
        self.agent.save(path)
    
    def plot_training(self):
        """학습 곡선 시각화"""
        plt.figure(figsize=(12, 4))
        
        # Reward
        plt.subplot(1, 3, 1)
        plt.plot(self.episode_rewards, alpha=0.3, label='Episode Reward')
        if len(self.episode_rewards) > 100:
            avg_rewards = [np.mean(self.episode_rewards[max(0, i-100):i+1]) 
                          for i in range(len(self.episode_rewards))]
            plt.plot(avg_rewards, label='Avg 100 Episodes')
        plt.xlabel('Episode')
        plt.ylabel('Reward')
        plt.legend()
        plt.grid()
        
        # Length
        plt.subplot(1, 3, 2)
        plt.plot(self.episode_lengths, alpha=0.5)
        plt.xlabel('Episode')
        plt.ylabel('Episode Length')
        plt.grid()
        
        # Loss
        plt.subplot(1, 3, 3)
        if len(self.losses) > 0:
            plt.plot(self.losses)
            plt.xlabel('Update')
            plt.ylabel('Loss')
            plt.grid()
        
        plt.tight_layout()
        plt.savefig(os.path.join(self.save_dir, 'training_curves.png'))
        print(f"Training curves saved to {self.save_dir}/training_curves.png")
        plt.close()


def main():
    """메인 실행"""
    trainer = Trainer(save_dir='checkpoints')
    trainer.train(
        num_episodes=1000,
        max_steps=1000,
        update_interval=2048
    )


if __name__ == '__main__':
    main()
