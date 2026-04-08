#!/bin/bash
# framesync server 启动脚本
# 自动检测端口占用，已在运行则跳过

PORT=9878
LOG=/tmp/framesync_server.log
SCRIPT="$(dirname "$0")/server.py"

PID=$(lsof -ti :$PORT)
if [ -n "$PID" ]; then
    echo "[framesync] 服务已在运行 (PID $PID)，跳过启动"
    echo "[framesync] http://localhost:$PORT"
    exit 0
fi

nohup python3 "$SCRIPT" > "$LOG" 2>&1 &
echo "[framesync] 已启动 PID=$! → http://localhost:$PORT"
echo "[framesync] 日志: $LOG"
