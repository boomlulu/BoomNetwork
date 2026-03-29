# Why Our Multiplayer Framework Doesn't Do Rollback

> For: dev.to / Medium / r/gamedev

---

The default answer for multiplayer game networking is "prediction + rollback." Photon Fusion does it. Netcode for GameObjects does it. GGPO made it the gold standard for fighting games.

**BoomNetwork takes the opposite approach — no rollback in the core layer.**

Not because we can't (we implemented it, then deleted it), but because we realized: for 80% of multiplayer games, rollback's complexity far outweighs the problem it solves.

## What Rollback Actually Solves

```
You press move → Client predicts → Wait for server → Server says "wrong" → Rollback → Resimulate
```

Rollback fixes **prediction errors**. But prediction errors only happen when you're not the authority. What if you *are* the authority?

## Self-Authority: What If You Are the Authority?

```
Traditional:
  I press move → Guess what the server will decide → Might be wrong → Rollback

BoomNetwork:
  I press move → I moved. Period. → Broadcast to others.
  Others receive → Trust it → Interpolate visually.
```

Three axioms:
1. **Own input = zero latency** — press a button, it happens, no waiting
2. **Remote state = trusted** — no questioning, no predicting others, just smooth visually
3. **Conflicts = one ruling** — an arbitrator (host or server) decides, no rollback

## The Trade-Off

| Dimension | Rollback Model | Self-Authority |
|-----------|---------------|----------------|
| Onboarding | Must understand prediction/rollback/snapshot/causality | `SendInput` + `OnFrame`, 30 lines of code |
| Code overhead | +30-40% sync code | Zero overhead |
| Debug complexity | 4× bug sources | Unchanged |
| Hit confirmation | Instant (local) | 1 RTT (wait for arbitrator) |
| Frame-perfect fairness | Yes | No (not a target) |

We accepted arbitration latency (mitigated by visual prediction) and gave up fighting-game-level fairness (not our target genre). In return: onboarding complexity dropped by an order of magnitude.

## Why This Matters in the AI Era

In 2026, more people write game code with AI assistants.

Getting Claude/GPT to correctly implement prediction + rollback? High chance of subtle desync bugs.

Getting AI to correctly implement `SendInput` + `OnFrame`? Almost impossible to get wrong.

**Simpler mental model = more reliable AI-generated code.** This isn't a coincidence — it's first principles resonating with the AI era.

## Numbers

- 4,000 concurrent players, 15-minute soak: **zero disconnections**
- Per-player: **0.68 KB/s up, 3.14 KB/s down**
- Server: **200 MB heap** for 4K players, rock-solid 20 fps
- Idle players: **zero bandwidth** (Silent When Idle)
- Hot path: **zero GC allocations** (Go server + C# codec)

## What It Covers

- Action/platformer/racing — self-authority = zero-latency controls
- MOBA/casual shooter — shooter authority + visual prediction
- Party/co-op/sandbox — minimal conflicts, latency doesn't matter
- Building/management — everyone does their own thing

What it doesn't cover (by design):
- Frame-perfect fighting games (need GGPO)
- Hardcore FPS (need precise lag compensation)

## Try It

```bash
# One-line server start
docker run -p 9000:9000 -p 9091:9091 ghcr.io/boomlulu/boomnetwork:latest
```

Unity install:
```
https://github.com/boomlulu/BoomNetwork.git?path=unity/com.boom.boomnetwork#dev1.0
```

[5-Minute Tutorial](https://github.com/boomlulu/BoomNetwork/blob/dev1.0/doc/tutorial.md) | [Full API for LLMs](https://github.com/boomlulu/BoomNetwork/blob/dev1.0/llms-full.txt)

GitHub: [github.com/boomlulu/BoomNetwork](https://github.com/boomlulu/BoomNetwork) — MIT License — Stars welcome!
