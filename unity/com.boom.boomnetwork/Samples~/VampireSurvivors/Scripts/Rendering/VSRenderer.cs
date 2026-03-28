// BoomNetwork VampireSurvivors Demo — Master 3D Renderer (Juice Edition)
//
// Isometric camera following local player. Kill explosions + screen shake.
// Gem magnet visual. Floating damage numbers. Growth-curve weapon scaling.
// Boss pulsing visual + warning banner.

using UnityEngine;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public class VSRenderer : MonoBehaviour
    {
        GameState _state;
        int _localSlot;
        bool _initialized;

        Camera _cam;

        // ==================== Materials ====================
        Material _matPlayer, _matPlayerHit;
        Material _matZombie, _matBat, _matMage, _matBoss;
        Material _matKnife, _matBoneShard;
        Material _matGem, _matGround;
        Material _matOrb, _matLightning, _matHolyWater;
        Material _matEnemyFlash; // white flash on death

        // ==================== Pools ====================
        GameObject[] _playerObjs = new GameObject[GameState.MaxPlayers];
        Material[] _playerMats = new Material[GameState.MaxPlayers];
        GameObject[] _enemyPool = new GameObject[GameState.MaxEnemies];
        Renderer[] _enemyRenderers = new Renderer[GameState.MaxEnemies];
        GameObject[] _projPool = new GameObject[GameState.MaxProjectiles];
        Renderer[] _projRenderers = new Renderer[GameState.MaxProjectiles];
        GameObject[] _gemPool = new GameObject[GameState.MaxGems];
        const int TotalOrbs = GameState.MaxPlayers * PlayerState.MaxOrbs;
        GameObject[] _orbPool = new GameObject[TotalOrbs];
        Material[] _orbMats = new Material[TotalOrbs];
        GameObject[] _flashPool = new GameObject[GameState.MaxLightningFlashes];

        // ==================== Camera (Feature 1) ====================
        static readonly Vector3 IsoOffset = new Vector3(0f, 18f, -14f);
        static readonly Quaternion IsoRotation = Quaternion.Euler(52f, 0f, 0f);
        const float IsoOrthoSize = 13f;
        const float CamSmoothSpeed = 8f;
        Vector3 _camCurrentPos;

        // ==================== Shadow Copy (delta detection) ====================
        int[] _prevEnemyHp = new int[GameState.MaxEnemies];
        bool[] _prevEnemyAlive = new bool[GameState.MaxEnemies];
        int[] _prevPlayerHp = new int[GameState.MaxPlayers];

        // ==================== Death Pop + Screen Shake (Feature 2) ====================
        struct DeathPop { public bool Active; public int Frame; public Vector3 Origin; public bool IsBoss; }
        DeathPop[] _deathPops = new DeathPop[GameState.MaxEnemies];
        Vector3 _shakeOffset;
        float _shakeIntensity;
        const float ShakePerKill = 0.08f;
        const float ShakeMax = 0.6f;

        // ==================== Gem Magnet (Feature 3) ====================
        Vector3[] _gemVisualPos = new Vector3[GameState.MaxGems];
        const float GemMagnetRadius = 4f;
        const float GemMagnetLerpSpeed = 8f;

        // ==================== Damage Numbers (Feature 4) ====================
        const int DmgNumPoolSize = 64;
        struct DamageNumber { public bool Active; public GameObject Obj; public TextMesh Text; public Vector3 Vel; public float TimeLeft; public float Total; }
        DamageNumber[] _dmgPool = new DamageNumber[DmgNumPoolSize];
        const float DmgNumDuration = 0.5f;
        const float DmgNumRiseSpeed = 2.5f;

        // ==================== Boss Warning (Feature 6) ====================
        float _bossWarningTimer;

        static readonly Color[] PlayerColors =
        {
            new Color(0.2f, 0.6f, 1f),
            new Color(0.2f, 1f, 0.4f),
            new Color(1f, 0.9f, 0.2f),
            new Color(1f, 0.4f, 0.9f),
        };

        // ==================== Init ====================

        public void Init(GameState state, int localSlot)
        {
            _state = state;
            _localSlot = localSlot;
            if (_initialized) return;
            _initialized = true;

            CreateMaterials();
            CreateCamera();
            CreateGround();
            CreateLight();
            CreatePlayerPool();
            CreateEnemyPool();
            CreateProjectilePool();
            CreateGemPool();
            CreateOrbPool();
            CreateFlashPool();
            CreateDamageNumberPool();

            // Snap camera to player or center
            if (_localSlot >= 0 && _localSlot < GameState.MaxPlayers && state.Players[_localSlot].IsActive)
                _camCurrentPos = new Vector3(state.Players[_localSlot].PosX.ToFloat(), 0f, state.Players[_localSlot].PosZ.ToFloat()) + IsoOffset;
            else
                _camCurrentPos = IsoOffset;
            _cam.transform.position = _camCurrentPos;
        }

        // ==================== Material Creation ====================

        void CreateMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            _matPlayer = new Material(shader) { color = PlayerColors[0] };
            _matPlayerHit = new Material(shader) { color = new Color(1f, 0.5f, 0.5f, 0.7f) };
            _matZombie = new Material(shader) { color = new Color(0.9f, 0.2f, 0.15f) };
            _matBat = new Material(shader) { color = new Color(0.5f, 0.1f, 0.6f) };
            _matMage = new Material(shader) { color = new Color(0.2f, 0.2f, 0.8f) };
            _matBoss = new Material(shader) { color = new Color(0.6f, 0f, 0f) };
            _matKnife = new Material(shader) { color = Color.white };
            _matBoneShard = new Material(shader) { color = new Color(0.9f, 0.85f, 0.7f) };
            _matGem = new Material(shader) { color = new Color(0.3f, 1f, 0.5f) };
            _matGround = new Material(shader) { color = new Color(0.15f, 0.15f, 0.2f) };
            _matOrb = new Material(shader) { color = new Color(0.4f, 0.7f, 1f) };
            _matOrb.SetFloat("_Smoothness", 0.9f);
            _matLightning = new Material(shader) { color = new Color(1f, 1f, 0.5f) };
            _matLightning.EnableKeyword("_EMISSION");
            _matHolyWater = new Material(shader) { color = new Color(0.3f, 0.5f, 1f, 0.5f) };
            _matEnemyFlash = new Material(shader) { color = Color.white };
        }

        // ==================== Camera (Feature 1: Isometric) ====================

        void CreateCamera()
        {
            var camObj = new GameObject("VS_Camera");
            camObj.transform.SetParent(transform);
            _cam = camObj.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
            _cam.orthographic = true;
            _cam.orthographicSize = IsoOrthoSize;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 80f;
            camObj.transform.rotation = IsoRotation;
            camObj.transform.position = IsoOffset;

            var mainCam = Camera.main;
            if (mainCam != null && mainCam != _cam) mainCam.gameObject.SetActive(false);
        }

        void SyncCamera()
        {
            if (_localSlot < 0 || _localSlot >= GameState.MaxPlayers) return;
            ref var p = ref _state.Players[_localSlot];
            if (!p.IsActive) return;

            Vector3 playerWorld = new Vector3(p.PosX.ToFloat(), 0f, p.PosZ.ToFloat());
            Vector3 target = playerWorld + IsoOffset + _shakeOffset;
            _camCurrentPos = Vector3.Lerp(_camCurrentPos, target, Time.deltaTime * CamSmoothSpeed);
            _cam.transform.position = _camCurrentPos;
            _cam.transform.rotation = IsoRotation;
        }

        // ==================== Scene Setup ====================

        void CreateGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ground.name = "VS_Ground";
            ground.transform.SetParent(transform);
            float size = GameState.ArenaHalfSize.ToFloat() * 2f;
            ground.transform.localScale = new Vector3(size, size, 1f);
            ground.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            ground.transform.position = new Vector3(0f, -0.01f, 0f);
            ground.GetComponent<Renderer>().sharedMaterial = _matGround;
            DestroyCollider(ground);

            float hs = GameState.ArenaHalfSize.ToFloat();
            MakeLine(0, 0, hs, size + 0.2f, 0.1f, 0.05f);
            MakeLine(0, 0, -hs, size + 0.2f, 0.1f, 0.05f);
            MakeLine(-hs, 0, 0, 0.05f, 0.1f, size + 0.2f);
            MakeLine(hs, 0, 0, 0.05f, 0.1f, size + 0.2f);
        }

        void MakeLine(float x, float y, float z, float sx, float sy, float sz)
        {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = "VS_Border";
            obj.transform.SetParent(transform);
            obj.transform.position = new Vector3(x, y, z);
            obj.transform.localScale = new Vector3(sx, sy, sz);
            obj.GetComponent<Renderer>().sharedMaterial = _matGem;
            DestroyCollider(obj);
        }

        void CreateLight()
        {
            var obj = new GameObject("VS_Light");
            obj.transform.SetParent(transform);
            obj.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = obj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.None;
        }

        // ==================== Object Pools ====================

        void CreatePlayerPool()
        {
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                obj.name = $"VS_Player_{i}";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.7f, 0.5f, 0.7f);
                var mat = new Material(_matPlayer) { color = PlayerColors[i] };
                obj.GetComponent<Renderer>().sharedMaterial = mat;
                _playerMats[i] = mat;
                DestroyCollider(obj);
                obj.SetActive(false);
                _playerObjs[i] = obj;
            }
        }

        void CreateEnemyPool()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "VS_Enemy";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                _enemyRenderers[i] = obj.GetComponent<Renderer>();
                _enemyRenderers[i].sharedMaterial = _matZombie;
                DestroyCollider(obj);
                obj.SetActive(false);
                _enemyPool[i] = obj;
            }
        }

        void CreateProjectilePool()
        {
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "VS_Proj";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.1f, 0.1f, 0.35f);
                _projRenderers[i] = obj.GetComponent<Renderer>();
                _projRenderers[i].sharedMaterial = _matKnife;
                DestroyCollider(obj);
                obj.SetActive(false);
                _projPool[i] = obj;
            }
        }

        void CreateGemPool()
        {
            for (int i = 0; i < GameState.MaxGems; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "VS_Gem";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
                obj.GetComponent<Renderer>().sharedMaterial = _matGem;
                DestroyCollider(obj);
                obj.SetActive(false);
                _gemPool[i] = obj;
            }
        }

        void CreateOrbPool()
        {
            for (int i = 0; i < TotalOrbs; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "VS_Orb";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
                var mat = new Material(_matOrb);
                mat.EnableKeyword("_EMISSION");
                obj.GetComponent<Renderer>().sharedMaterial = mat;
                _orbMats[i] = mat;
                DestroyCollider(obj);
                obj.SetActive(false);
                _orbPool[i] = obj;
            }
        }

        void CreateFlashPool()
        {
            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "VS_Flash";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.6f, 2f, 0.6f);
                obj.GetComponent<Renderer>().sharedMaterial = _matLightning;
                DestroyCollider(obj);
                obj.SetActive(false);
                _flashPool[i] = obj;
            }
        }

        void CreateDamageNumberPool()
        {
            for (int i = 0; i < DmgNumPoolSize; i++)
            {
                var obj = new GameObject("VS_DmgNum");
                obj.transform.SetParent(transform);
                var tm = obj.AddComponent<TextMesh>();
                tm.fontSize = 48;
                tm.characterSize = 0.05f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                obj.SetActive(false);
                _dmgPool[i] = new DamageNumber { Obj = obj, Text = tm };
            }
        }

        // ==================== SyncVisuals ====================

        public void SyncVisuals()
        {
            if (!_initialized || _state == null) return;

            SyncCamera();
            SyncPlayers();
            SyncEnemies();
            SyncProjectiles();
            SyncGems();
            SyncOrbs();
            SyncFlashes();
            UpdateDeathExplosions();
            UpdateDamageNumbers();
            UpdateShake();
            UpdateBossWarning();
            CaptureFrameShadow();
        }

        // ==================== Sync Methods ====================

        void SyncPlayers()
        {
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref _state.Players[i];
                bool show = p.IsActive && p.IsAlive;
                _playerObjs[i].SetActive(show);
                if (!show) continue;

                // Feature 5a: player scale by level
                float pScale = 1f + Mathf.Min(p.Level - 1, 9) * 0.015f;
                _playerObjs[i].transform.localScale = new Vector3(0.7f * pScale, 0.5f * pScale, 0.7f * pScale);
                _playerObjs[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());

                if (p.FacingX != FInt.Zero || p.FacingZ != FInt.Zero)
                {
                    float angle = Mathf.Atan2(p.FacingX.ToFloat(), p.FacingZ.ToFloat()) * Mathf.Rad2Deg;
                    _playerObjs[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                }

                var rend = _playerObjs[i].GetComponent<Renderer>();
                rend.sharedMaterial = p.InvincibilityFrames > 0 && (_state.FrameNumber % 4 < 2)
                    ? _matPlayerHit : _playerMats[i];
            }
        }

        void SyncEnemies()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                bool show = e.IsAlive;

                // Don't hide if death pop is active (UpdateDeathExplosions manages it)
                if (!_deathPops[i].Active) _enemyPool[i].SetActive(show);

                // Feature 2: detect death transition
                if (!e.IsAlive && _prevEnemyAlive[i])
                {
                    Vector3 lastPos = _enemyPool[i].transform.position;
                    bool isBoss = (e.Type == EnemyType.Boss);
                    _deathPops[i] = new DeathPop { Active = true, Frame = 0, Origin = lastPos, IsBoss = isBoss };
                    _shakeIntensity = Mathf.Min(_shakeIntensity + (isBoss ? ShakeMax : ShakePerKill), ShakeMax);
                }

                if (!show) continue;

                float ex = e.PosX.ToFloat(), ez = e.PosZ.ToFloat();

                switch (e.Type)
                {
                    case EnemyType.Zombie:
                        _enemyPool[i].transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.45f, ez);
                        _enemyRenderers[i].sharedMaterial = _matZombie;
                        break;
                    case EnemyType.Bat:
                        _enemyPool[i].transform.localScale = new Vector3(0.5f, 0.4f, 0.5f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.8f, ez);
                        _enemyRenderers[i].sharedMaterial = _matBat;
                        break;
                    case EnemyType.SkeletonMage:
                        _enemyPool[i].transform.localScale = new Vector3(0.6f, 1.1f, 0.6f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.45f, ez);
                        _enemyRenderers[i].sharedMaterial = _matMage;
                        break;
                    case EnemyType.Boss:
                        // Feature 6: big pulsing boss
                        float bossS = 3f + Mathf.Sin(_state.FrameNumber * 0.15f) * 0.15f;
                        _enemyPool[i].transform.localScale = new Vector3(bossS, bossS * 1.2f, bossS);
                        _enemyPool[i].transform.position = new Vector3(ex, bossS * 0.6f, ez);
                        float pulse = (Mathf.Sin(_state.FrameNumber * 0.2f) + 1f) * 0.5f;
                        _enemyRenderers[i].material.color = Color.Lerp(new Color(0.15f, 0f, 0f), new Color(0.8f, 0f, 0f), pulse);
                        break;
                }
            }
        }

        void SyncProjectiles()
        {
            for (int i = 0; i < GameState.MaxProjectiles; i++)
            {
                ref var p = ref _state.Projectiles[i];
                bool show = p.IsAlive;
                _projPool[i].SetActive(show);
                if (!show) continue;

                switch (p.Type)
                {
                    case ProjectileType.Knife:
                    {
                        // Feature 5b: knife scale by level
                        int kLvl = GetWeaponLevel(p.OwnerPlayerId, WeaponType.Knife);
                        float kS = Mathf.Lerp(1f, 2f, (kLvl - 1) / 4f);
                        float kLen = kLvl >= 3 ? 0.35f * kS * 1.5f : 0.35f * kS;
                        _projPool[i].transform.localScale = new Vector3(0.1f * kS, 0.1f * kS, kLen);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());
                        _projRenderers[i].sharedMaterial = _matKnife;
                        if (p.DirX != FInt.Zero || p.DirZ != FInt.Zero)
                        {
                            float angle = Mathf.Atan2(p.DirX.ToFloat(), p.DirZ.ToFloat()) * Mathf.Rad2Deg;
                            _projPool[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                        }
                        break;
                    }
                    case ProjectileType.BoneShard:
                        _projPool[i].transform.localScale = new Vector3(0.15f, 0.15f, 0.25f);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.6f, p.PosZ.ToFloat());
                        _projRenderers[i].sharedMaterial = _matBoneShard;
                        if (p.DirX != FInt.Zero || p.DirZ != FInt.Zero)
                        {
                            float angle = Mathf.Atan2(p.DirX.ToFloat(), p.DirZ.ToFloat()) * Mathf.Rad2Deg;
                            _projPool[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                        }
                        break;

                    case ProjectileType.HolyPuddle:
                    {
                        float diameter = p.Radius.ToFloat() * 2f;
                        _projPool[i].transform.localScale = new Vector3(diameter, 0.05f, diameter);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.02f, p.PosZ.ToFloat());
                        _projPool[i].transform.rotation = Quaternion.identity;
                        // Feature 5e: holy water color intensity by level
                        float hwT = Mathf.Clamp01((p.Radius.ToFloat() - 2f) / 3f);
                        _projRenderers[i].material.color = Color.Lerp(
                            new Color(0.3f, 0.5f, 1f, 0.5f),
                            new Color(0.5f, 0.9f, 1f, 0.8f), hwT);
                        break;
                    }
                }
            }
        }

        void SyncGems()
        {
            // Feature 3: gem magnet visual
            for (int i = 0; i < GameState.MaxGems; i++)
            {
                ref var g = ref _state.Gems[i];
                bool show = g.IsAlive;
                _gemPool[i].SetActive(show);
                if (!show)
                {
                    _gemVisualPos[i] = new Vector3(g.PosX.ToFloat(), 0.2f, g.PosZ.ToFloat());
                    continue;
                }

                float simX = g.PosX.ToFloat(), simZ = g.PosZ.ToFloat();
                float bob = Mathf.Sin((_state.FrameNumber + i * 7) * 0.15f) * 0.1f;
                Vector3 simPos = new Vector3(simX, 0.2f + bob, simZ);

                // Find closest player within magnet radius
                Vector3 pullTarget = simPos;
                float bestSq = GemMagnetRadius * GemMagnetRadius;
                bool inMagnet = false;
                for (int p = 0; p < GameState.MaxPlayers; p++)
                {
                    ref var pl = ref _state.Players[p];
                    if (!pl.IsActive || !pl.IsAlive) continue;
                    float dx = pl.PosX.ToFloat() - simX;
                    float dz = pl.PosZ.ToFloat() - simZ;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < bestSq) { bestSq = dSq; pullTarget = new Vector3(pl.PosX.ToFloat(), 0.3f, pl.PosZ.ToFloat()); inMagnet = true; }
                }

                Vector3 targetVisual = inMagnet ? pullTarget : simPos;
                _gemVisualPos[i] = Vector3.Lerp(_gemVisualPos[i], targetVisual, Time.deltaTime * GemMagnetLerpSpeed);
                _gemPool[i].transform.position = _gemVisualPos[i];
            }
        }

        void SyncOrbs()
        {
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var player = ref _state.Players[p];
                for (int o = 0; o < PlayerState.MaxOrbs; o++)
                {
                    int poolIdx = p * PlayerState.MaxOrbs + o;
                    var orb = player.GetOrb(o);
                    bool show = player.IsActive && player.IsAlive && orb.Active;
                    _orbPool[poolIdx].SetActive(show);
                    if (!show) continue;

                    float rad = orb.AngleDeg.ToFloat() * Mathf.Deg2Rad;
                    float ox = player.PosX.ToFloat() + Mathf.Cos(rad) * GameState.OrbOrbitRadius.ToFloat();
                    float oz = player.PosZ.ToFloat() + Mathf.Sin(rad) * GameState.OrbOrbitRadius.ToFloat();
                    _orbPool[poolIdx].transform.position = new Vector3(ox, 0.5f, oz);

                    // Feature 5c: orb size + emission by level
                    int orbLvl = GetWeaponLevel(p, WeaponType.Orb);
                    float orbScale = 0.35f + (orbLvl - 1) * 0.06f;
                    _orbPool[poolIdx].transform.localScale = new Vector3(orbScale, orbScale, orbScale);
                    float emission = (orbLvl - 1) * 0.25f;
                    _orbMats[poolIdx].SetColor("_EmissionColor", new Color(0.4f, 0.7f, 1f) * emission);
                }
            }
        }

        void SyncFlashes()
        {
            // Feature 5d: lightning bolt width by level
            float lvl = GetMaxWeaponLevel(WeaponType.Lightning);
            float boltW = 0.6f + (lvl - 1) * 0.15f;
            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
            {
                ref var f = ref _state.Flashes[i];
                bool show = f.FramesLeft > 0;
                _flashPool[i].SetActive(show);
                if (show)
                {
                    _flashPool[i].transform.position = new Vector3(f.PosX.ToFloat(), 1f, f.PosZ.ToFloat());
                    _flashPool[i].transform.localScale = new Vector3(boltW, 2f, boltW);
                }
            }
        }

        // ==================== Feature 2: Death Explosions ====================

        void UpdateDeathExplosions()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var pop = ref _deathPops[i];
                if (!pop.Active) continue;
                pop.Frame++;
                var obj = _enemyPool[i];
                float maxScale = pop.IsBoss ? 4.5f : 1.5f;

                if (pop.Frame == 1)
                {
                    obj.SetActive(true);
                    obj.transform.position = pop.Origin;
                    _enemyRenderers[i].sharedMaterial = _matEnemyFlash;
                }
                else if (pop.Frame <= 4)
                {
                    float t = (pop.Frame - 1) / 3f;
                    float s = Mathf.Lerp(1f, maxScale, t);
                    obj.SetActive(true);
                    obj.transform.localScale = new Vector3(0.7f * s, 0.9f * s, 0.7f * s);
                    float fade = 1f - t;
                    _enemyRenderers[i].sharedMaterial = _matEnemyFlash;
                }
                else
                {
                    obj.SetActive(false);
                    obj.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                    pop.Active = false;
                }
            }
        }

        // ==================== Feature 2: Screen Shake ====================

        void UpdateShake()
        {
            if (_shakeIntensity <= 0.001f) { _shakeOffset = Vector3.zero; _shakeIntensity = 0f; return; }
            _shakeOffset = new Vector3(
                (Random.value * 2f - 1f) * _shakeIntensity,
                0f,
                (Random.value * 2f - 1f) * _shakeIntensity);
            _shakeIntensity *= 0.6f;
        }

        // ==================== Feature 4: Damage Numbers ====================

        void UpdateDamageNumbers()
        {
            // Detect enemy damage
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                if (_prevEnemyAlive[i] && e.IsAlive && e.Hp < _prevEnemyHp[i])
                {
                    int dmg = _prevEnemyHp[i] - e.Hp;
                    bool isLightning = IsNearLightningFlash(e.PosX.ToFloat(), e.PosZ.ToFloat());
                    SpawnDamageNumber(_enemyPool[i].transform.position, dmg,
                        isLightning ? Color.yellow : Color.white);
                }
            }

            // Detect player damage
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var pl = ref _state.Players[p];
                if (!pl.IsActive) continue;
                if (pl.Hp < _prevPlayerHp[p] && _prevPlayerHp[p] > 0)
                    SpawnDamageNumber(_playerObjs[p].transform.position,
                        _prevPlayerHp[p] - pl.Hp, new Color(1f, 0.3f, 0.3f));
            }

            // Tick active numbers
            float dt = Time.deltaTime;
            for (int i = 0; i < DmgNumPoolSize; i++)
            {
                ref var d = ref _dmgPool[i];
                if (!d.Active) continue;
                d.TimeLeft -= dt;
                if (d.TimeLeft <= 0f) { d.Active = false; d.Obj.SetActive(false); continue; }
                d.Obj.transform.position += d.Vel * dt;
                // Billboard: face camera
                d.Obj.transform.rotation = _cam.transform.rotation;
                // Fade out
                var c = d.Text.color; c.a = d.TimeLeft / d.Total; d.Text.color = c;
            }
        }

        void SpawnDamageNumber(Vector3 worldPos, int amount, Color color)
        {
            for (int i = 0; i < DmgNumPoolSize; i++)
            {
                ref var d = ref _dmgPool[i];
                if (d.Active) continue;
                d.Active = true;
                d.TimeLeft = DmgNumDuration;
                d.Total = DmgNumDuration;
                d.Vel = new Vector3((Random.value - 0.5f) * 0.5f, DmgNumRiseSpeed, (Random.value - 0.5f) * 0.3f);
                d.Text.text = amount.ToString();
                d.Text.color = color;
                d.Obj.transform.position = worldPos + Vector3.up;
                d.Obj.SetActive(true);
                return;
            }
        }

        bool IsNearLightningFlash(float x, float z)
        {
            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
            {
                ref var f = ref _state.Flashes[i];
                if (f.FramesLeft == 0) continue;
                float dx = f.PosX.ToFloat() - x, dz = f.PosZ.ToFloat() - z;
                if (dx * dx + dz * dz < 2.25f) return true; // 1.5^2
            }
            return false;
        }

        // ==================== Feature 6: Boss Warning ====================

        void UpdateBossWarning()
        {
            // Show warning at start of boss waves
            if (_state.WaveNumber > 0 && _state.WaveNumber % GameState.BossWaveInterval == 0 && IsBossAlive())
                _bossWarningTimer = 2f;
            if (_bossWarningTimer > 0f) _bossWarningTimer -= Time.deltaTime;
        }

        bool IsBossAlive()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (_state.Enemies[i].IsAlive && _state.Enemies[i].Type == EnemyType.Boss)
                    return true;
            return false;
        }

        GUIStyle _bossWarnStyle;

        void OnGUI()
        {
            if (!_initialized || _bossWarningTimer <= 0f) return;
            if (_bossWarnStyle == null)
                _bossWarnStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 32, fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.red }
                };
            float alpha = Mathf.Clamp01(_bossWarningTimer);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(Screen.width / 2f - 150f, Screen.height * 0.15f, 300f, 60f),
                "! BOSS INCOMING !", _bossWarnStyle);
            GUI.color = prev;
        }

        // ==================== Shadow Copy ====================

        void CaptureFrameShadow()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                _prevEnemyHp[i] = _state.Enemies[i].Hp;
                _prevEnemyAlive[i] = _state.Enemies[i].IsAlive;
            }
            for (int i = 0; i < GameState.MaxPlayers; i++)
                _prevPlayerHp[i] = _state.Players[i].Hp;
        }

        // ==================== Helpers ====================

        int GetWeaponLevel(int playerId, WeaponType wt)
        {
            if (playerId < 0 || playerId >= GameState.MaxPlayers) return 1;
            ref var pl = ref _state.Players[playerId];
            int slot = pl.FindWeaponSlot(wt);
            return slot >= 0 ? pl.GetWeapon(slot).Level : 1;
        }

        float GetMaxWeaponLevel(WeaponType wt)
        {
            float max = 1f;
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var pl = ref _state.Players[i];
                if (!pl.IsActive) continue;
                int slot = pl.FindWeaponSlot(wt);
                if (slot >= 0) max = Mathf.Max(max, pl.GetWeapon(slot).Level);
            }
            return max;
        }

        static void DestroyCollider(GameObject obj)
        {
            var col = obj.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        void OnDestroy()
        {
            Destroy(_matPlayer); Destroy(_matPlayerHit);
            Destroy(_matZombie); Destroy(_matBat); Destroy(_matMage); Destroy(_matBoss);
            Destroy(_matKnife); Destroy(_matBoneShard);
            Destroy(_matGem); Destroy(_matGround);
            Destroy(_matOrb); Destroy(_matLightning); Destroy(_matHolyWater);
            Destroy(_matEnemyFlash);
            for (int i = 0; i < _playerMats.Length; i++)
                if (_playerMats[i] != null) Destroy(_playerMats[i]);
            for (int i = 0; i < _orbMats.Length; i++)
                if (_orbMats[i] != null) Destroy(_orbMats[i]);
        }
    }
}
