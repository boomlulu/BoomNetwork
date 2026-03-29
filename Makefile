.PHONY: test test-go test-cs build run lint docker clean

# Run all tests
test: test-go test-cs

test-go:
	cd svr && go test -race -count=1 ./...

test-cs:
	cd cli && dotnet test

# Build server binary
build:
	cd svr && CGO_ENABLED=0 go build -ldflags="-s -w" -o ../bin/boomnetwork-server ./cmd/framesync/

# Run server locally
run:
	cd svr && go run ./cmd/framesync/ -config cmd/framesync/config.yaml

# Lint
lint:
	cd svr && go vet ./...

# Docker
docker:
	docker build -t boomnetwork:local ./svr

# Check goreleaser config
check-release:
	goreleaser check

# Clean build artifacts
clean:
	rm -rf bin/ dist/
