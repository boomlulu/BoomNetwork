# 多阶段构建: 编译 → 最小运行镜像
FROM golang:1.24-alpine AS builder

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 go build -ldflags="-s -w" -o /framesync ./cmd/framesync/

# 运行镜像
FROM alpine:3.20

RUN apk add --no-cache ca-certificates tzdata
COPY --from=builder /framesync /usr/local/bin/framesync
COPY cmd/framesync/config.yaml /etc/boomnetwork/config.yaml

EXPOSE 9000
EXPOSE 9090

ENTRYPOINT ["framesync"]
CMD ["-config", "/etc/boomnetwork/config.yaml"]
