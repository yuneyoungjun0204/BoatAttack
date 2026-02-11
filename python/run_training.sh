#!/bin/bash
# Unity RL Training Launcher

echo "=== Unity RL Training ==="
echo ""

# 가상환경 활성화
source ../rl_env/bin/activate

# 체크포인트 디렉토리 생성
mkdir -p checkpoints

# 학습 시작
echo "Starting training..."
echo "Press Ctrl+C to stop and save checkpoint"
echo ""

python train.py

echo ""
echo "Training finished!"
