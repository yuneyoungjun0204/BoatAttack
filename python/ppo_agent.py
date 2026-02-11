#!/usr/bin/env python3
"""
Simple PPO Agent for Unity RL
"""

import torch
import torch.nn as nn
import torch.optim as optim
import numpy as np
from torch.distributions import Normal

class ActorCritic(nn.Module):
    """Actor-Critic 네트워크"""
    def __init__(self, obs_dim, action_dim, hidden_dim=256):
        super(ActorCritic, self).__init__()
        
        # 공유 특징 추출기
        self.shared = nn.Sequential(
            nn.Linear(obs_dim, hidden_dim),
            nn.ReLU(),
            nn.Linear(hidden_dim, hidden_dim),
            nn.ReLU()
        )
        
        # Actor (정책)
        self.actor_mean = nn.Linear(hidden_dim, action_dim)
        self.actor_logstd = nn.Parameter(torch.zeros(action_dim))
        
        # Critic (가치 함수)
        self.critic = nn.Linear(hidden_dim, 1)
        
    def forward(self, state):
        """순전파"""
        features = self.shared(state)
        
        # Actor
        action_mean = torch.tanh(self.actor_mean(features))  # -1 ~ 1
        action_std = torch.exp(self.actor_logstd)
        
        # Critic
        value = self.critic(features)
        
        return action_mean, action_std, value
    
    def get_action(self, state, deterministic=False):
        """액션 샘플링"""
        state = torch.FloatTensor(state).unsqueeze(0)
        
        with torch.no_grad():
            action_mean, action_std, value = self.forward(state)
        
        if deterministic:
            action = action_mean
        else:
            dist = Normal(action_mean, action_std)
            action = dist.sample()
        
        action = torch.clamp(action, -1.0, 1.0)
        
        return action.squeeze(0).numpy(), value.item()


class PPOAgent:
    """PPO 에이전트"""
    def __init__(self, obs_dim, action_dim, lr=3e-4, gamma=0.99, lambda_=0.95):
        self.actor_critic = ActorCritic(obs_dim, action_dim)
        self.optimizer = optim.Adam(self.actor_critic.parameters(), lr=lr)
        
        self.gamma = gamma
        self.lambda_ = lambda_
        
        # 경험 버퍼
        self.states = []
        self.actions = []
        self.rewards = []
        self.values = []
        self.dones = []
        
    def select_action(self, state):
        """액션 선택"""
        action, value = self.actor_critic.get_action(state)
        
        # 버퍼에 저장
        self.states.append(state)
        self.actions.append(action)
        self.values.append(value)
        
        return action
    
    def store_transition(self, reward, done):
        """보상과 done 저장"""
        self.rewards.append(reward)
        self.dones.append(done)
    
    def update(self, epochs=10, batch_size=64):
        """PPO 업데이트"""
        if len(self.states) == 0:
            return {'loss': 0.0}
        
        # GAE 계산
        returns, advantages = self._compute_gae()
        
        # 텐서 변환
        states = torch.FloatTensor(np.array(self.states))
        actions = torch.FloatTensor(np.array(self.actions))
        returns = torch.FloatTensor(returns)
        advantages = torch.FloatTensor(advantages)
        advantages = (advantages - advantages.mean()) / (advantages.std() + 1e-8)
        
        # Old policy 저장
        with torch.no_grad():
            old_action_mean, old_action_std, _ = self.actor_critic(states)
            old_dist = Normal(old_action_mean, old_action_std)
            old_log_probs = old_dist.log_prob(actions).sum(dim=1)
        
        # PPO 업데이트
        total_loss = 0.0
        for epoch in range(epochs):
            # 현재 정책
            action_mean, action_std, values = self.actor_critic(states)
            dist = Normal(action_mean, action_std)
            log_probs = dist.log_prob(actions).sum(dim=1)
            entropy = dist.entropy().mean()
            
            # Ratio
            ratio = torch.exp(log_probs - old_log_probs)
            
            # Clipped surrogate loss
            surr1 = ratio * advantages
            surr2 = torch.clamp(ratio, 0.8, 1.2) * advantages
            actor_loss = -torch.min(surr1, surr2).mean()
            
            # Value loss
            critic_loss = nn.MSELoss()(values.squeeze(), returns)
            
            # Total loss
            loss = actor_loss + 0.5 * critic_loss - 0.01 * entropy
            
            # 업데이트
            self.optimizer.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(self.actor_critic.parameters(), 0.5)
            self.optimizer.step()
            
            total_loss += loss.item()
        
        # 버퍼 초기화
        self.clear_buffer()
        
        return {
            'loss': total_loss / epochs,
            'actor_loss': actor_loss.item(),
            'critic_loss': critic_loss.item()
        }
    
    def _compute_gae(self):
        """Generalized Advantage Estimation"""
        returns = []
        advantages = []
        
        gae = 0
        next_value = 0
        
        for t in reversed(range(len(self.rewards))):
            delta = self.rewards[t] + self.gamma * next_value * (1 - self.dones[t]) - self.values[t]
            gae = delta + self.gamma * self.lambda_ * (1 - self.dones[t]) * gae
            
            advantages.insert(0, gae)
            returns.insert(0, gae + self.values[t])
            
            next_value = self.values[t]
        
        return returns, advantages
    
    def clear_buffer(self):
        """버퍼 초기화"""
        self.states = []
        self.actions = []
        self.rewards = []
        self.values = []
        self.dones = []
    
    def save(self, path):
        """모델 저장"""
        torch.save({
            'model_state_dict': self.actor_critic.state_dict(),
            'optimizer_state_dict': self.optimizer.state_dict()
        }, path)
        print(f"Model saved to {path}")
    
    def load(self, path):
        """모델 로드"""
        checkpoint = torch.load(path)
        self.actor_critic.load_state_dict(checkpoint['model_state_dict'])
        self.optimizer.load_state_dict(checkpoint['optimizer_state_dict'])
        print(f"Model loaded from {path}")
