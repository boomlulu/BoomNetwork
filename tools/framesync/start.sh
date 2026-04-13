#!/bin/bash
# framesync server 启动脚本
# 用法:
#   ./start.sh          → 启动（已在运行则跳过）
#   ./start.sh restart  → 强制 kill 旧进程后重启

PORT=9878
LOG=/tmp/framesync_server.log
SCRIPT="$(dirname "$0")/server.py"

PID=$(lsof -ti :$PORT)

if [ "$1" = "restart" ]; then
    if [ -n "$PID" ]; then
        echo "$PID" | xargs kill && echo "[framesync] 已终止旧进程 PID=$(echo $PID | tr '\n' ' ')"
        sleep 0.5
    fi
elif [ -n "$PID" ]; then
    echo "[framesync] 服务已在运行 (PID $PID)，跳过启动"
    echo "[framesync] 如需重启: $0 restart"
    echo "[framesync] http://localhost:$PORT"
    exit 0
fi

nohup python3 "$SCRIPT" > "$LOG" 2>&1 &
echo "[framesync] 已启动 PID=$! → http://localhost:$PORT"
echo "[framesync] 日志: $LOG"
