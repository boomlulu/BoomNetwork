// BoomNetwork VampireSurvivors Demo — Master 3D Renderer (Juice Edition)
//
// Isometric camera following local player. Kill explosions + screen shake.
// Gem magnet visual. Floating damage numbers. Growth-curve weapon scaling.
// Boss pulsing visual + warning banner.
// NEW: LinkBeam line, ShieldWall, HealAura ring, FrostNova ring, FocusFire marker,
//      RevivalTotem pillar, TwinCore dual orbs, SplitBoss/SplitHalf rendering.

using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public enum RenderStrategy { Auto, MainThread, Jobs }

    public class VSRenderer : MonoBehaviour
    {
        // ==================== Strategy + Perf ====================

        [SerializeField] RenderStrategy _strategyType = RenderStrategy.Auto;
        bool _useJobs;

        // Jobs: enemy TransformAccessArray
        TransformAccessArray _enemyTransformArray;
        NativeArray<EnemyJobData> _enemyJobData;   // positions + scales packed
        NativeArray<byte>         _enemyJobAlive;
        bool _jobsInitialized;

        // Perf accumulation (Stopwatch per sub-system)
        readonly Stopwatch _swEnemies  = new Stopwatch();
        readonly Stopwatch _swProj     = new Stopwatch();
        readonly Stopwatch _swGems     = new Stopwatch();
        readonly Stopwatch _swPlayers  = new Stopwatch();
        readonly Stopwatch _swTotal    = new Stopwatch();
        double _accumEnemies, _accumProj, _accumGems, _accumPlayers, _accumTotal;
        int _perfFrameCount;

        // ==================== Enemy Job Struct ====================

        struct EnemyJobData   // blittable (6 floats)
        {
            public float PosX, PosY, PosZ;
            public float SX, SY, SZ;
        }

        [BurstCompile]
        struct SyncEnemyTransformsJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<EnemyJobData> Data;
            [ReadOnly] public NativeArray<byte>         IsAlive;
            public void Execute(int i, TransformAccess t)
            {
                if (IsAlive[i] == 0) return;
                var d = Data[i];
                t.position   = new Vector3(d.PosX, d.PosY, d.PosZ);
                t.localScale = new Vector3(d.SX,   d.SY,   d.SZ);
            }
        }


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
        Material _matEnemyFlash;
        // New weapon materials
        Material _matLinkBeam;       // 青绿光束
        Material _matHealAura;       // 绿色治疗光环
        Material _matShieldWall;     // 蓝色护盾墙
        Material _matFocusFireMark;  // 红色集火标记
        Material _matRevivalTotem;   // 金色复活图腾
        Material _matFrostNova;      // 蓝白冰冻光环
        Material _matFireTrail;      // 橙色火焰地面
        Material _matTwinCoreA;      // TwinCore 核心 A（红色）
        Material _matTwinCoreB;      // TwinCore 核心 B（紫色）
        Material _matTwinCoreHit;    // TwinCore 命中后高亮（白色）
        Material _matSplitBoss;      // 分裂 Boss（暗红橙）
        Material _matSplitHalf;      // 分裂半体（橙色）
        Material _matSplitShotMain;  // 分裂弹主弹
        Material _matSplitShotSplinter; // 分裂弹碎片

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

        // New pools
        const int MaxLinkBeamLines = GameState.MaxPlayers * (GameState.MaxPlayers - 1) / 2; // 6 pairs
        GameObject[] _linkBeamPool = new GameObject[MaxLinkBeamLines];
        const int MaxShieldWalls = MaxLinkBeamLines;
        GameObject[] _shieldWallPool = new GameObject[MaxShieldWalls];
        GameObject[] _revivalTotemPool = new GameObject[GameState.MaxRevivalTotems];
        GameObject[] _focusFireMarkPool = new GameObject[1]; // 只有 1 个集火目标
        Material _focusFireMarkMat;
        // FrostNova ring (per player, pulsing effect handled by scale)
        const int MaxFrostRings = GameState.MaxPlayers;
        GameObject[] _frostRingPool = new GameObject[MaxFrostRings];
        float[] _frostRingScale = new float[MaxFrostRings];

        // ==================== Camera ====================
        static readonly Vector3 IsoOffset = new Vector3(0f, 18f, -14f);
        static readonly Quaternion IsoRotation = Quaternion.Euler(52f, 0f, 0f);
        const float IsoOrthoSize = 13f;
        const float CamSmoothSpeed = 8f;
        Vector3 _camCurrentPos;

        // ==================== Player Interpolation ====================
        Vector3[] _playerPrevPos = new Vector3[GameState.MaxPlayers];
        Vector3[] _playerCurPos = new Vector3[GameState.MaxPlayers];
        Quaternion[] _playerPrevRot = new Quaternion[GameState.MaxPlayers];
        Quaternion[] _playerCurRot = new Quaternion[GameState.MaxPlayers];
        float _interpT;
        float _simFrameInterval;
        float _timeSinceLastSync;
        float _prevFrameWallTime;
        float _curFrameWallTime;
        float _measuredJitter;
        float _jitterBuffer;
        const float JitterEmaAlpha   = 0.15f;
        const float JitterSmoothAlpha = 0.1f;
        const float JitterMinSec     = 0.010f;
        const float JitterMaxFraction = 0.75f;

        float _lastSyncTime;
        int _syncCount;
        float _diagTimer;

        Vector3[] _lastRenderPos = new Vector3[GameState.MaxPlayers];

        // ==================== Shadow Copy ====================
        int[] _prevEnemyHp = new int[GameState.MaxEnemies];
        bool[] _prevEnemyAlive = new bool[GameState.MaxEnemies];
        int[] _prevPlayerHp = new int[GameState.MaxPlayers];

        // ==================== Enemy HP Bars (Feature 7) ====================
        static readonly Quaternion HpBarRot = Quaternion.Euler(52f, 0f, 0f);
        const float HpBarHideDelay = 3f;
        const float HpBarWidth = 0.8f;
        const float HpBarHeight = 0.1f;
        int[] _enemyMaxHp = new int[GameState.MaxEnemies];
        float[] _enemyHpBarTimer = new float[GameState.MaxEnemies];
        GameObject[] _hpBarBgObjs = new GameObject[GameState.MaxEnemies];
        GameObject[] _hpBarFillObjs = new GameObject[GameState.MaxEnemies];
        SpriteRenderer[] _hpBarFill = new SpriteRenderer[GameState.MaxEnemies];
        static Sprite _barSprite;

        // ==================== Death Pop + Screen Shake ====================
        struct DeathPop { public bool Active; public int Frame; public Vector3 Origin; public bool IsBoss; }
        DeathPop[] _deathPops = new DeathPop[GameState.MaxEnemies];
        Vector3 _shakeOffset;
        float _shakeIntensity;
        const float ShakePerKill = 0.08f;
        const float ShakeMax = 0.6f;

        // ==================== Gem Magnet ====================
        Vector3[] _gemVisualPos = new Vector3[GameState.MaxGems];
        bool[] _gemWasAlive = new bool[GameState.MaxGems];
        const float GemMagnetRadius = 4f;
        const float GemMagnetLerpSpeed = 8f;

        // ==================== Damage Numbers ====================
        const int DmgNumPoolSize = 64;
        struct DamageNumber { public bool Active; public GameObject Obj; public TextMesh Text; public Vector3 Vel; public float TimeLeft; public float Total; }
        DamageNumber[] _dmgPool = new DamageNumber[DmgNumPoolSize];
        const float DmgNumDuration = 0.5f;
        const float DmgNumRiseSpeed = 2.5f;

        // ==================== Boss Warning ====================
        float _bossWarningTimer;

        static readonly Color[] PlayerColors =
        {
            new Color(0.2f, 0.6f, 1f),
            new Color(0.2f, 1f, 0.4f),
            new Color(1f, 0.9f, 0.2f),
            new Color(1f, 0.4f, 0.9f),
        };

        // ==================== Init ====================

        public void Init(GameState state, int localSlot, float simFrameInterval = 0.05f)
        {
            _state = state;
            _localSlot = localSlot;
            _simFrameInterval = simFrameInterval;
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
            CreateNewWeaponPools();
            CreateHpBars();

            // Resolve strategy: Jobs not supported on WebGL
            _useJobs = _strategyType == RenderStrategy.Jobs
                    || (_strategyType == RenderStrategy.Auto
                        && Application.platform != RuntimePlatform.WebGLPlayer);

            if (_useJobs) InitEnemyJobs();

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                _playerPrevRot[i] = Quaternion.identity;
                _playerCurRot[i] = Quaternion.identity;
            }
            _jitterBuffer = JitterMinSec;

            if (_localSlot >= 0 && _localSlot < GameState.MaxPlayers && state.Players[_localSlot].IsActive)
                _camCurrentPos = new Vector3(state.Players[_localSlot].PosX.ToFloat(), 0f, state.Players[_localSlot].PosZ.ToFloat()) + IsoOffset;
            else
                _camCurrentPos = IsoOffset;
            _cam.transform.position = _camCurrentPos;
        }

        // ==================== Jobs Init ====================

        void InitEnemyJobs()
        {
            if (_jobsInitialized) return;
            _jobsInitialized = true;
            var transforms = new Transform[GameState.MaxEnemies];
            for (int i = 0; i < GameState.MaxEnemies; i++)
                transforms[i] = _enemyPool[i].transform;
            _enemyTransformArray = new TransformAccessArray(transforms);
            _enemyJobData  = new NativeArray<EnemyJobData>(GameState.MaxEnemies, Allocator.Persistent);
            _enemyJobAlive = new NativeArray<byte>(GameState.MaxEnemies, Allocator.Persistent);
        }

        /// <summary>Switch render strategy at runtime (e.g. from UI toggle).</summary>
        public void SetStrategy(RenderStrategy s)
        {
            _strategyType = s;
            bool wantJobs = s == RenderStrategy.Jobs
                || (s == RenderStrategy.Auto && Application.platform != RuntimePlatform.WebGLPlayer);
            if (wantJobs && !_jobsInitialized) InitEnemyJobs();
            _useJobs = wantJobs && _jobsInitialized;
        }

        // ==================== Material Creation ====================

        void CreateMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            _matPlayer      = new Material(shader) { color = PlayerColors[0] };
            _matPlayerHit   = new Material(shader) { color = new Color(1f, 0.5f, 0.5f, 0.7f) };
            _matZombie      = new Material(shader) { color = new Color(0.9f, 0.2f, 0.15f) };
            _matBat         = new Material(shader) { color = new Color(0.5f, 0.1f, 0.6f) };
            _matMage        = new Material(shader) { color = new Color(0.2f, 0.2f, 0.8f) };
            _matBoss        = new Material(shader) { color = new Color(0.6f, 0f, 0f) };
            _matKnife       = new Material(shader) { color = Color.white };
            _matBoneShard   = new Material(shader) { color = new Color(0.9f, 0.85f, 0.7f) };
            _matGem         = new Material(shader) { color = new Color(0.3f, 1f, 0.5f) };
            _matGround      = new Material(shader) { color = new Color(0.15f, 0.15f, 0.2f) };
            _matOrb         = new Material(shader) { color = new Color(0.4f, 0.7f, 1f) };
            _matOrb.SetFloat("_Smoothness", 0.9f);
            _matLightning   = new Material(shader) { color = new Color(1f, 1f, 0.5f) };
            _matLightning.EnableKeyword("_EMISSION");
            _matHolyWater   = new Material(shader) { color = new Color(0.3f, 0.5f, 1f, 0.5f) };
            _matEnemyFlash  = new Material(shader) { color = Color.white };

            // New weapon materials
            _matLinkBeam       = new Material(shader) { color = new Color(0f, 1f, 0.9f, 0.8f) };
            _matLinkBeam.EnableKeyword("_EMISSION");
            _matHealAura       = new Material(shader) { color = new Color(0.2f, 1f, 0.4f, 0.5f) };
            _matShieldWall     = new Material(shader) { color = new Color(0.3f, 0.6f, 1f, 0.6f) };
            _matFocusFireMark  = new Material(shader) { color = new Color(1f, 0.2f, 0.1f) };
            _matFocusFireMark.EnableKeyword("_EMISSION");
            _matRevivalTotem   = new Material(shader) { color = new Color(1f, 0.85f, 0.1f) };
            _matRevivalTotem.EnableKeyword("_EMISSION");
            _matFrostNova      = new Material(shader) { color = new Color(0.5f, 0.85f, 1f, 0.4f) };
            _matFireTrail      = new Material(shader) { color = new Color(1f, 0.5f, 0f, 0.6f) };
            _matTwinCoreA      = new Material(shader) { color = new Color(1f, 0.15f, 0.1f) };
            _matTwinCoreA.EnableKeyword("_EMISSION");
            _matTwinCoreB      = new Material(shader) { color = new Color(0.8f, 0.1f, 0.9f) };
            _matTwinCoreB.EnableKeyword("_EMISSION");
            _matTwinCoreHit    = new Material(shader) { color = Color.white };
            _matTwinCoreHit.EnableKeyword("_EMISSION");
            _matSplitBoss      = new Material(shader) { color = new Color(0.9f, 0.4f, 0f) };
            _matSplitHalf      = new Material(shader) { color = new Color(1f, 0.55f, 0.1f) };
            _matSplitShotMain      = new Material(shader) { color = new Color(1f, 0.85f, 0f) };
            _matSplitShotSplinter  = new Material(shader) { color = new Color(1f, 0.6f, 0.1f) };
        }

        // ==================== Camera ====================

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

        Vector3 _camTarget;

        void SyncCamera()
        {
            if (_localSlot < 0 || _localSlot >= GameState.MaxPlayers) return;
            ref var p = ref _state.Players[_localSlot];
            if (!p.IsActive) return;
            _camTarget = new Vector3(p.PosX.ToFloat(), 0f, p.PosZ.ToFloat()) + IsoOffset;
        }

        void Update()
        {
            if (!_initialized || _state == null) return;

            // Feature 7: Tick HP bar hide timers
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                if (_enemyHpBarTimer[i] <= 0f) continue;
                _enemyHpBarTimer[i] -= Time.deltaTime;
                if (_enemyHpBarTimer[i] <= 0f)
                {
                    _hpBarBgObjs[i].SetActive(false);
                    _hpBarFillObjs[i].SetActive(false);
                }
            }

            _timeSinceLastSync += Time.deltaTime;
            float window = _curFrameWallTime - _prevFrameWallTime;
            float renderTime = Time.realtimeSinceStartup - _jitterBuffer;
            _interpT = (window > 0.001f)
                ? Mathf.Clamp01((renderTime - _prevFrameWallTime) / window)
                : 1f;

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                if (_playerObjs[i] == null || !_playerObjs[i].activeSelf) continue;
                Vector3 interpPos = Vector3.Lerp(_playerPrevPos[i], _playerCurPos[i], _interpT);
                _playerObjs[i].transform.position = interpPos;
                Quaternion interpRot = Quaternion.Slerp(_playerPrevRot[i], _playerCurRot[i], _interpT);
                _playerObjs[i].transform.rotation = interpRot;
                _lastRenderPos[i] = interpPos;
            }

            // Animate FrostNova rings
            for (int i = 0; i < MaxFrostRings; i++)
            {
                if (_frostRingPool[i] == null || !_frostRingPool[i].activeSelf) continue;
                _frostRingScale[i] -= Time.deltaTime * 3f;
                if (_frostRingScale[i] <= 0f) { _frostRingPool[i].SetActive(false); continue; }
                float s = _frostRingScale[i];
                _frostRingPool[i].transform.localScale = new Vector3(s, 0.05f, s);
            }
        }

        void LateUpdate()
        {
            if (!_initialized || _cam == null) return;
            if (_localSlot >= 0 && _localSlot < GameState.MaxPlayers
                && _playerObjs[_localSlot] != null && _playerObjs[_localSlot].activeSelf)
            {
                _camTarget = _playerObjs[_localSlot].transform.position
                    - new Vector3(0f, 0.5f, 0f) + IsoOffset;
            }
            UpdateShake();
            _cam.transform.position = _camTarget + _shakeOffset;
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
            obj.name = "VS_Border"; obj.transform.SetParent(transform);
            obj.transform.position = new Vector3(x, y, z);
            obj.transform.localScale = new Vector3(sx, sy, sz);
            obj.GetComponent<Renderer>().sharedMaterial = _matGem;
            DestroyCollider(obj);
        }

        void CreateLight()
        {
            var obj = new GameObject("VS_Light"); obj.transform.SetParent(transform);
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
                obj.name = $"VS_Player_{i}"; obj.transform.SetParent(transform);
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
                obj.name = "VS_Enemy"; obj.transform.SetParent(transform);
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
                obj.name = "VS_Proj"; obj.transform.SetParent(transform);
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
                obj.name = "VS_Gem"; obj.transform.SetParent(transform);
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
                obj.name = "VS_Orb"; obj.transform.SetParent(transform);
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
                obj.name = "VS_Flash"; obj.transform.SetParent(transform);
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
                var obj = new GameObject("VS_DmgNum"); obj.transform.SetParent(transform);
                var tm = obj.AddComponent<TextMesh>();
                tm.fontSize = 48; tm.characterSize = 0.05f;
                tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
                obj.SetActive(false);
                _dmgPool[i] = new DamageNumber { Obj = obj, Text = tm };
            }
        }

        void CreateHpBars()
        {
            if (_barSprite == null)
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _barSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }

            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                // Background (dark red)
                var bg = new GameObject("VS_HpBarBg");
                bg.transform.SetParent(transform);
                var bgSr = bg.AddComponent<SpriteRenderer>();
                bgSr.sprite = _barSprite;
                bgSr.color = new Color(0.4f, 0.05f, 0.05f);
                bgSr.sortingOrder = 5;
                bg.transform.localScale = new Vector3(HpBarWidth, HpBarHeight, 1f);
                bg.transform.rotation = HpBarRot;
                bg.SetActive(false);
                _hpBarBgObjs[i] = bg;

                // Fill (green → yellow → red based on HP ratio)
                var fill = new GameObject("VS_HpBarFill");
                fill.transform.SetParent(transform);
                var fillSr = fill.AddComponent<SpriteRenderer>();
                fillSr.sprite = _barSprite;
                fillSr.color = new Color(0.1f, 0.85f, 0.2f);
                fillSr.sortingOrder = 6;
                fill.transform.rotation = HpBarRot;
                fill.SetActive(false);
                _hpBarFillObjs[i] = fill;
                _hpBarFill[i] = fillSr;
            }
        }

        void CreateNewWeaponPools()
        {
            // LinkBeam lines (thin horizontal cubes)
            for (int i = 0; i < MaxLinkBeamLines; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "VS_LinkBeam"; obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matLinkBeam;
                DestroyCollider(obj);
                obj.SetActive(false);
                _linkBeamPool[i] = obj;
            }

            // ShieldWall (thin vertical planes)
            for (int i = 0; i < MaxShieldWalls; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "VS_ShieldWall"; obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matShieldWall;
                DestroyCollider(obj);
                obj.SetActive(false);
                _shieldWallPool[i] = obj;
            }

            // Revival totems (golden pillars)
            for (int i = 0; i < GameState.MaxRevivalTotems; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "VS_RevivalTotem"; obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.4f, 1.5f, 0.4f);
                obj.GetComponent<Renderer>().sharedMaterial = _matRevivalTotem;
                DestroyCollider(obj);
                obj.SetActive(false);
                _revivalTotemPool[i] = obj;
            }

            // FocusFire mark (tall red spike above enemy)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "VS_FocusFireMark"; obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.2f, 1.5f, 0.2f);
                _focusFireMarkMat = new Material(_matFocusFireMark);
                _focusFireMarkMat.EnableKeyword("_EMISSION");
                obj.GetComponent<Renderer>().sharedMaterial = _focusFireMarkMat;
                DestroyCollider(obj);
                obj.SetActive(false);
                _focusFireMarkPool[0] = obj;
            }

            // FrostNova rings (flat disk, animated in Update)
            for (int i = 0; i < MaxFrostRings; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "VS_FrostRing"; obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matFrostNova;
                DestroyCollider(obj);
                obj.SetActive(false);
                _frostRingPool[i] = obj;
            }
        }

        // ==================== SyncVisuals ====================

        public void SyncVisuals()
        {
            if (!_initialized || _state == null) return;

            _swTotal.Restart();

            _swPlayers.Restart();
            SyncPlayers();
            _swPlayers.Stop();

            _swEnemies.Restart();
            SyncEnemies();
            _swEnemies.Stop();

            _swProj.Restart();
            SyncProjectiles();
            _swProj.Stop();

            _swGems.Restart();
            SyncGems();
            _swGems.Stop();

            SyncOrbs();
            SyncFlashes();
            SyncNewWeaponEffects();
            UpdateDeathExplosions();
            UpdateDamageNumbers();
            UpdateBossWarning();
            CaptureFrameShadow();

            _swTotal.Stop();
            _accumPlayers += _swPlayers.Elapsed.TotalMilliseconds;
            _accumEnemies += _swEnemies.Elapsed.TotalMilliseconds;
            _accumProj    += _swProj.Elapsed.TotalMilliseconds;
            _accumGems    += _swGems.Elapsed.TotalMilliseconds;
            _accumTotal   += _swTotal.Elapsed.TotalMilliseconds;

            _perfFrameCount++;
            if (_perfFrameCount >= 100)
            {
                string strategy = _useJobs ? "Jobs" : "MainThread";
                UnityEngine.Debug.Log(
                    $"[VSRenderer Perf|{strategy}] avg over 100 frames:\n" +
                    $"  SyncPlayers:  {_accumPlayers / 100:F3} ms\n" +
                    $"  SyncEnemies:  {_accumEnemies / 100:F3} ms\n" +
                    $"  SyncProj:     {_accumProj    / 100:F3} ms\n" +
                    $"  SyncGems:     {_accumGems    / 100:F3} ms\n" +
                    $"  Total:        {_accumTotal   / 100:F3} ms");
                _accumPlayers = _accumEnemies = _accumProj = _accumGems = _accumTotal = 0;
                _perfFrameCount = 0;
            }
        }

        // ==================== Sync Methods ====================

        void SyncPlayers()
        {
            float now = Time.realtimeSinceStartup;
            float syncDelta = now - _lastSyncTime;
            _lastSyncTime = now; _syncCount++; _diagTimer += syncDelta;
            if (_diagTimer >= 2f)
            {
                float avgHz = _syncCount / _diagTimer;
                Debug.Log($"[VS-Jitter] avg rate: {avgHz:F1} Hz, interval: {syncDelta*1000f:F1}ms");
                _diagTimer = 0f; _syncCount = 0;
            }

            _prevFrameWallTime = _curFrameWallTime;
            float newArrival = Time.realtimeSinceStartup;
            if (_curFrameWallTime > 0f && _simFrameInterval > 0f)
            {
                float actualInterval = newArrival - _curFrameWallTime;
                float deviation = Mathf.Abs(actualInterval - _simFrameInterval);
                _measuredJitter = Mathf.Lerp(_measuredJitter, deviation, JitterEmaAlpha);
                float targetBuffer = Mathf.Clamp(_measuredJitter * 2f, JitterMinSec, _simFrameInterval * JitterMaxFraction);
                _jitterBuffer = Mathf.Lerp(_jitterBuffer, targetBuffer, JitterSmoothAlpha);
            }
            _curFrameWallTime = newArrival;
            _timeSinceLastSync = 0f;

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref _state.Players[i];
                bool show = p.IsActive && p.IsAlive;
                _playerObjs[i].SetActive(show);
                if (!show) { _lastRenderPos[i] = Vector3.zero; continue; }

                float pScale = 1f + Mathf.Min(p.Level - 1, 9) * 0.015f;
                _playerObjs[i].transform.localScale = new Vector3(0.7f * pScale, 0.5f * pScale, 0.7f * pScale);

                Vector3 newPos = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());
                _playerPrevPos[i] = _playerCurPos[i];
                _playerCurPos[i] = newPos;
                if (_playerPrevPos[i] == Vector3.zero) _playerPrevPos[i] = newPos;

                Quaternion newRot;
                if (p.FacingX != FInt.Zero || p.FacingZ != FInt.Zero)
                {
                    float angle = Mathf.Atan2(p.FacingX.ToFloat(), p.FacingZ.ToFloat()) * Mathf.Rad2Deg;
                    newRot = Quaternion.Euler(0f, angle, 0f);
                }
                else newRot = _playerCurRot[i];
                _playerPrevRot[i] = _playerCurRot[i];
                _playerCurRot[i] = newRot;

                var rend = _playerObjs[i].GetComponent<Renderer>();
                rend.sharedMaterial = p.InvincibilityFrames > 0 && (_state.FrameNumber % 4 < 2)
                    ? _matPlayerHit : _playerMats[i];
            }
        }

        void SyncEnemies()
        {
            if (_useJobs)
                SyncEnemiesJobs();
            else
                SyncEnemiesMainThread();
        }

        // --- Main-thread path (original logic) ---
        void SyncEnemiesMainThread()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                bool show = e.IsAlive;
                if (!_deathPops[i].Active) _enemyPool[i].SetActive(show);

                if (!e.IsAlive && _prevEnemyAlive[i])
                {
                    Vector3 lastPos = _enemyPool[i].transform.position;
                    bool isBoss = (e.Type == EnemyType.Boss || e.Type == EnemyType.TwinCore
                        || e.Type == EnemyType.SplitBoss || e.Type == EnemyType.SplitHalf);
                    _deathPops[i] = new DeathPop { Active = true, Frame = 0, Origin = lastPos, IsBoss = isBoss };
                    _shakeIntensity = Mathf.Min(_shakeIntensity + (isBoss ? ShakeMax : ShakePerKill), ShakeMax);

                    // Feature 7: hide HP bar on death
                    _enemyHpBarTimer[i] = 0f;
                    _hpBarBgObjs[i].SetActive(false);
                    _hpBarFillObjs[i].SetActive(false);
                }

                if (!show) continue;

                float ex = e.PosX.ToFloat(), ez = e.PosZ.ToFloat();
                float barY = 1.3f; // default HP bar height above ground

                switch (e.Type)
                {
                    case EnemyType.Zombie:
                        _enemyPool[i].transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.45f, ez);
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matZombie;
                        barY = 1.2f;
                        break;
                    case EnemyType.Bat:
                        _enemyPool[i].transform.localScale = new Vector3(0.5f, 0.4f, 0.5f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.8f, ez);
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matBat;
                        barY = 1.2f;
                        break;
                    case EnemyType.SkeletonMage:
                        _enemyPool[i].transform.localScale = new Vector3(0.6f, 1.1f, 0.6f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.45f, ez);
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matMage;
                        barY = 1.5f;
                        break;
                    case EnemyType.Boss:
                        float bossS = 3f + Mathf.Sin(_state.FrameNumber * 0.15f) * 0.15f;
                        _enemyPool[i].transform.localScale = new Vector3(bossS, bossS * 1.2f, bossS);
                        _enemyPool[i].transform.position = new Vector3(ex, bossS * 0.6f, ez);
                        float pulse = (Mathf.Sin(_state.FrameNumber * 0.2f) + 1f) * 0.5f;
                        _enemyRenderers[i].material.color = Color.Lerp(new Color(0.15f, 0f, 0f), new Color(0.8f, 0f, 0f), pulse);
                        barY = bossS * 1.2f + 0.5f;
                        break;

                    case EnemyType.TwinCore:
                        // 红色/紫色双核，被标记时高亮白色
                        bool isLinkedA = e.LinkedEnemyIdx >= 0;
                        float coreS = 1.5f + Mathf.Sin(_state.FrameNumber * 0.2f) * 0.1f;
                        _enemyPool[i].transform.localScale = new Vector3(coreS, coreS, coreS);
                        _enemyPool[i].transform.position = new Vector3(ex, coreS * 0.5f, ez);
                        if (e.HitWindowTimer > 0)
                            _enemyRenderers[i].sharedMaterial = _matTwinCoreHit;
                        else
                            _enemyRenderers[i].sharedMaterial = (i % 2 == 0) ? _matTwinCoreA : _matTwinCoreB;
                        barY = coreS * 1.2f + 0.3f;
                        break;

                    case EnemyType.SplitBoss:
                        float sbS = 2.5f + Mathf.Sin(_state.FrameNumber * 0.12f) * 0.2f;
                        _enemyPool[i].transform.localScale = new Vector3(sbS, sbS * 1.1f, sbS);
                        _enemyPool[i].transform.position = new Vector3(ex, sbS * 0.55f, ez);
                        // 闪烁提示快分裂
                        float splitT = e.BehaviorTimer / (float)GameState.SplitBossSplitTimer;
                        float r = Mathf.Lerp(1f, 0.5f, splitT);
                        _enemyRenderers[i].material.color = new Color(r, 0.35f * splitT, 0f);
                        barY = sbS * 1.2f + 0.3f;
                        break;

                    case EnemyType.SplitHalf:
                        float shS = 1.8f;
                        _enemyPool[i].transform.localScale = new Vector3(shS, shS, shS);
                        _enemyPool[i].transform.position = new Vector3(ex, shS * 0.5f, ez);
                        // 死亡窗口期间闪烁红色
                        if (e.HitWindowTimer > 0 && (_state.FrameNumber % 6 < 3))
                            _enemyRenderers[i].material.color = Color.red;
                        else
                            _enemyRenderers[i].sharedMaterial = _matSplitHalf;
                        barY = shS * 1.2f + 0.2f;
                        break;
                }

                // Feature 7: Enemy HP bars
                // Record maxHp on spawn (first frame alive)
                if (!_prevEnemyAlive[i] && e.IsAlive)
                    _enemyMaxHp[i] = e.Hp;

                // Detect damage → reset hide timer
                if (e.IsAlive && _prevEnemyAlive[i] && e.Hp < _prevEnemyHp[i])
                    _enemyHpBarTimer[i] = HpBarHideDelay;

                bool showBar = e.IsAlive && _enemyMaxHp[i] > 0 && e.Hp < _enemyMaxHp[i] && _enemyHpBarTimer[i] > 0f;
                _hpBarBgObjs[i].SetActive(showBar);
                _hpBarFillObjs[i].SetActive(showBar);

                if (showBar)
                {
                    float ratio = Mathf.Clamp01((float)e.Hp / _enemyMaxHp[i]);
                    float fillW = HpBarWidth * ratio;

                    // BG: centered at enemy position
                    _hpBarBgObjs[i].transform.position = new Vector3(ex, barY, ez);

                    // Fill: left-anchored (pivot center, so offset by half fill width from left edge)
                    float leftEdge = ex - HpBarWidth * 0.5f;
                    _hpBarFillObjs[i].transform.position = new Vector3(leftEdge + fillW * 0.5f, barY + 0.001f, ez);
                    _hpBarFillObjs[i].transform.localScale = new Vector3(fillW, HpBarHeight, 1f);

                    // Color: green → yellow → red
                    _hpBarFill[i].color = Color.Lerp(new Color(0.9f, 0.1f, 0.1f), new Color(0.1f, 0.85f, 0.2f), ratio);
                }
            }
        }

        // --- Jobs path: Pass1 (SetActive + materials + CopyIn), Pass2 Job (transform writes) ---
        void SyncEnemiesJobs()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                bool show = e.IsAlive;
                if (!_deathPops[i].Active) _enemyPool[i].SetActive(show);

                if (!e.IsAlive && _prevEnemyAlive[i])
                {
                    Vector3 lastPos = _enemyPool[i].transform.position;
                    bool isBoss = (e.Type == EnemyType.Boss || e.Type == EnemyType.TwinCore
                        || e.Type == EnemyType.SplitBoss || e.Type == EnemyType.SplitHalf);
                    _deathPops[i] = new DeathPop { Active = true, Frame = 0, Origin = lastPos, IsBoss = isBoss };
                    _shakeIntensity = Mathf.Min(_shakeIntensity + (isBoss ? ShakeMax : ShakePerKill), ShakeMax);
                }

                _enemyJobAlive[i] = (byte)(show ? 1 : 0);
                if (!show) continue;

                // FInt.ToFloat() = Raw / 1024f (SCALE=1024)
                float ex = e.PosX.Raw * (1f / FInt.SCALE);
                float ez = e.PosZ.Raw * (1f / FInt.SCALE);
                float ey, sx, sy, sz;

                switch (e.Type)
                {
                    case EnemyType.Zombie:
                        ey = 0.45f; sx = 0.7f; sy = 0.9f; sz = 0.7f;
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matZombie;
                        break;
                    case EnemyType.Bat:
                        ey = 0.8f; sx = 0.5f; sy = 0.4f; sz = 0.5f;
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matBat;
                        break;
                    case EnemyType.SkeletonMage:
                        ey = 0.45f; sx = 0.6f; sy = 1.1f; sz = 0.6f;
                        _enemyRenderers[i].sharedMaterial = e.SlowFrames > 0 ? _matFrostNova : _matMage;
                        break;
                    case EnemyType.Boss:
                    {
                        float bossS = 3f + Mathf.Sin(_state.FrameNumber * 0.15f) * 0.15f;
                        ey = bossS * 0.6f; sx = bossS; sy = bossS * 1.2f; sz = bossS;
                        float pulse = (Mathf.Sin(_state.FrameNumber * 0.2f) + 1f) * 0.5f;
                        _enemyRenderers[i].material.color = Color.Lerp(new Color(0.15f, 0f, 0f), new Color(0.8f, 0f, 0f), pulse);
                        break;
                    }
                    case EnemyType.TwinCore:
                    {
                        float coreS = 1.5f + Mathf.Sin(_state.FrameNumber * 0.2f) * 0.1f;
                        ey = coreS * 0.5f; sx = coreS; sy = coreS; sz = coreS;
                        _enemyRenderers[i].sharedMaterial = e.HitWindowTimer > 0 ? _matTwinCoreHit
                            : (i % 2 == 0 ? _matTwinCoreA : _matTwinCoreB);
                        break;
                    }
                    case EnemyType.SplitBoss:
                    {
                        float sbS = 2.5f + Mathf.Sin(_state.FrameNumber * 0.12f) * 0.2f;
                        ey = sbS * 0.55f; sx = sbS; sy = sbS * 1.1f; sz = sbS;
                        float splitT2 = e.BehaviorTimer / (float)GameState.SplitBossSplitTimer;
                        _enemyRenderers[i].material.color = new Color(Mathf.Lerp(1f, 0.5f, splitT2), 0.35f * splitT2, 0f);
                        break;
                    }
                    default: // SplitHalf
                    {
                        const float shS = 1.8f;
                        ey = shS * 0.5f; sx = shS; sy = shS; sz = shS;
                        if (e.HitWindowTimer > 0 && (_state.FrameNumber % 6 < 3))
                            _enemyRenderers[i].material.color = Color.red;
                        else
                            _enemyRenderers[i].sharedMaterial = _matSplitHalf;
                        break;
                    }
                }

                _enemyJobData[i] = new EnemyJobData { PosX = ex, PosY = ey, PosZ = ez, SX = sx, SY = sy, SZ = sz };
            }

            // Burst-compiled parallel transform write
            new SyncEnemyTransformsJob
            {
                Data    = _enemyJobData,
                IsAlive = _enemyJobAlive,
            }.Schedule(_enemyTransformArray).Complete();
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
                        float hwT = Mathf.Clamp01((p.Radius.ToFloat() - 2f) / 3f);
                        _projRenderers[i].material.color = Color.Lerp(
                            new Color(0.3f, 0.5f, 1f, 0.5f), new Color(0.5f, 0.9f, 1f, 0.8f), hwT);
                        break;
                    }
                    case ProjectileType.FireTrailPuddle:
                    {
                        float diameter = p.Radius.ToFloat() * 2f;
                        _projPool[i].transform.localScale = new Vector3(diameter, 0.06f, diameter);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.03f, p.PosZ.ToFloat());
                        _projPool[i].transform.rotation = Quaternion.identity;
                        float fade = p.LifetimeFrames / (float)GameState.FireTrailLifetime;
                        _projRenderers[i].material.color = new Color(1f, 0.4f + fade * 0.2f, 0f, 0.5f + fade * 0.3f);
                        break;
                    }
                    case ProjectileType.SplitShotMain:
                        _projPool[i].transform.localScale = new Vector3(0.18f, 0.18f, 0.32f);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());
                        _projRenderers[i].sharedMaterial = _matSplitShotMain;
                        if (p.DirX != FInt.Zero || p.DirZ != FInt.Zero)
                        {
                            float angle = Mathf.Atan2(p.DirX.ToFloat(), p.DirZ.ToFloat()) * Mathf.Rad2Deg;
                            _projPool[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                        }
                        break;
                    case ProjectileType.SplitShotSplinter:
                        _projPool[i].transform.localScale = new Vector3(0.1f, 0.1f, 0.18f);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());
                        _projRenderers[i].sharedMaterial = _matSplitShotSplinter;
                        if (p.DirX != FInt.Zero || p.DirZ != FInt.Zero)
                        {
                            float angle = Mathf.Atan2(p.DirX.ToFloat(), p.DirZ.ToFloat()) * Mathf.Rad2Deg;
                            _projPool[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                        }
                        break;
                }
            }
        }

        void SyncGems()
        {
            for (int i = 0; i < GameState.MaxGems; i++)
            {
                ref var g = ref _state.Gems[i];
                bool show = g.IsAlive;
                _gemPool[i].SetActive(show);
                if (!show) { _gemWasAlive[i] = false; continue; }

                float simX = g.PosX.ToFloat(), simZ = g.PosZ.ToFloat();
                float bob = Mathf.Sin((_state.FrameNumber + i * 7) * 0.15f) * 0.1f;
                Vector3 simPos = new Vector3(simX, 0.2f + bob, simZ);

                if (!_gemWasAlive[i]) { _gemVisualPos[i] = simPos; _gemWasAlive[i] = true; }
                else
                {
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
                }
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

        // ==================== New Weapon Visual Effects ====================

        void SyncNewWeaponEffects()
        {
            SyncLinkBeams();
            SyncShieldWalls();
            SyncRevivalTotems();
            SyncFocusFireMark();
            SyncFrostNovaRings();
        }

        void SyncLinkBeams()
        {
            int beamIdx = 0;
            for (int a = 0; a < GameState.MaxPlayers && beamIdx < MaxLinkBeamLines; a++)
            {
                ref var pA = ref _state.Players[a];
                bool aHasBeam = pA.IsActive && pA.IsAlive && pA.FindWeaponSlot(WeaponType.LinkBeam) >= 0;

                for (int b = a + 1; b < GameState.MaxPlayers && beamIdx < MaxLinkBeamLines; b++)
                {
                    ref var pB = ref _state.Players[b];
                    bool bHasBeam = pB.IsActive && pB.IsAlive && pB.FindWeaponSlot(WeaponType.LinkBeam) >= 0;

                    bool show = aHasBeam || bHasBeam;
                    show = show && pA.IsActive && pA.IsAlive && pB.IsActive && pB.IsAlive;
                    _linkBeamPool[beamIdx].SetActive(show);

                    if (show)
                    {
                        Vector3 posA = new Vector3(pA.PosX.ToFloat(), 0.5f, pA.PosZ.ToFloat());
                        Vector3 posB = new Vector3(pB.PosX.ToFloat(), 0.5f, pB.PosZ.ToFloat());
                        Vector3 mid = (posA + posB) * 0.5f;
                        float len = (posB - posA).magnitude;
                        Vector3 dir = (posB - posA).normalized;

                        _linkBeamPool[beamIdx].transform.position = mid;
                        _linkBeamPool[beamIdx].transform.localScale = new Vector3(0.06f, 0.06f, len);
                        if (dir.sqrMagnitude > 0.001f)
                            _linkBeamPool[beamIdx].transform.rotation = Quaternion.LookRotation(dir);

                        // 近距离时光束更亮
                        float dist = len;
                        bool close = dist < GameState.LinkBeamCloseDist.ToFloat();
                        _linkBeamPool[beamIdx].GetComponent<Renderer>().material.color =
                            close ? new Color(0f, 1f, 0.8f, 1f) : new Color(0f, 0.7f, 0.6f, 0.6f);
                    }
                    beamIdx++;
                }
            }
            for (; beamIdx < MaxLinkBeamLines; beamIdx++)
                _linkBeamPool[beamIdx].SetActive(false);
        }

        void SyncShieldWalls()
        {
            int wallIdx = 0;
            for (int a = 0; a < GameState.MaxPlayers && wallIdx < MaxShieldWalls; a++)
            {
                ref var pA = ref _state.Players[a];
                bool aHasWall = pA.IsActive && pA.IsAlive && pA.FindWeaponSlot(WeaponType.ShieldWall) >= 0;

                for (int b = a + 1; b < GameState.MaxPlayers && wallIdx < MaxShieldWalls; b++)
                {
                    ref var pB = ref _state.Players[b];
                    bool bHasWall = pB.IsActive && pB.IsAlive && pB.FindWeaponSlot(WeaponType.ShieldWall) >= 0;

                    bool show = (aHasWall || bHasWall) && pA.IsActive && pA.IsAlive && pB.IsActive && pB.IsAlive;
                    _shieldWallPool[wallIdx].SetActive(show);

                    if (show)
                    {
                        Vector3 posA = new Vector3(pA.PosX.ToFloat(), 0f, pA.PosZ.ToFloat());
                        Vector3 posB = new Vector3(pB.PosX.ToFloat(), 0f, pB.PosZ.ToFloat());
                        Vector3 mid = (posA + posB) * 0.5f + Vector3.up;
                        float len = (posB - posA).magnitude;
                        Vector3 dir = (posB - posA).normalized;

                        _shieldWallPool[wallIdx].transform.position = mid;
                        // 竖立的盾墙：沿连线方向，高 2，薄 0.1
                        _shieldWallPool[wallIdx].transform.localScale = new Vector3(0.1f, 2f, len * 0.33f);
                        if (dir.sqrMagnitude > 0.001f)
                            _shieldWallPool[wallIdx].transform.rotation = Quaternion.LookRotation(dir);

                        float alpha = 0.5f + 0.3f * Mathf.Sin(Time.time * 4f);
                        _shieldWallPool[wallIdx].GetComponent<Renderer>().material.color =
                            new Color(0.3f, 0.6f, 1f, alpha);
                    }
                    wallIdx++;
                }
            }
            for (; wallIdx < MaxShieldWalls; wallIdx++)
                _shieldWallPool[wallIdx].SetActive(false);
        }

        void SyncRevivalTotems()
        {
            for (int i = 0; i < GameState.MaxRevivalTotems; i++)
            {
                ref var totem = ref _state.RevivalTotems[i];
                _revivalTotemPool[i].SetActive(totem.Active);
                if (!totem.Active) continue;

                float px = totem.PosX.ToFloat(), pz = totem.PosZ.ToFloat();
                _revivalTotemPool[i].transform.position = new Vector3(px, 1.5f, pz);

                // 进度越高越亮
                float progress = totem.ReviveProgress / (float)GameState.RevivalRequiredFrames;
                float emissionIntensity = progress * 3f;
                _revivalTotemPool[i].GetComponent<Renderer>().material.SetColor("_EmissionColor",
                    new Color(1f, 0.8f, 0f) * emissionIntensity);
                _revivalTotemPool[i].GetComponent<Renderer>().material.color =
                    Color.Lerp(new Color(0.6f, 0.5f, 0f), new Color(1f, 0.9f, 0.2f), progress);

                // 复活进度环：用缩放表示
                float ringS = 0.4f + progress * 1.5f;
                _revivalTotemPool[i].transform.localScale = new Vector3(0.4f, 1.5f + progress * 0.8f, 0.4f);
            }
        }

        void SyncFocusFireMark()
        {
            bool hasFocus = _state.FocusFireTarget >= 0 && _state.FocusFireTarget < GameState.MaxEnemies
                && _state.Enemies[_state.FocusFireTarget].IsAlive && _state.FocusFireTimer > 0;

            _focusFireMarkPool[0].SetActive(hasFocus);
            if (hasFocus)
            {
                ref var e = ref _state.Enemies[_state.FocusFireTarget];
                float blink = Mathf.Sin(Time.time * 8f) * 0.5f + 0.5f;
                _focusFireMarkPool[0].transform.position = new Vector3(
                    e.PosX.ToFloat(), 3f + blink * 0.3f, e.PosZ.ToFloat());
                _focusFireMarkPool[0].transform.localScale = new Vector3(0.25f, 1.5f, 0.25f);
                float alpha = _state.FocusFireTimer / (float)GameState.FocusFireDuration;
                _focusFireMarkMat.SetColor("_EmissionColor", new Color(1f, 0.1f, 0f) * (2f + blink));
                _focusFireMarkMat.color = new Color(1f, 0.2f, 0.1f, alpha);
            }
        }

        void SyncFrostNovaRings()
        {
            // FrostNova ring: triggered when a player fires FrostNova
            // We detect it by checking if any enemy just got SlowFrames applied this frame
            // (detected via prev vs current, but we don't track that here)
            // Instead: keep rings active per-player with FrostNova weapon, pulsing every cooldown
            for (int i = 0; i < MaxFrostRings; i++)
            {
                // If ring is animating (handled in Update via _frostRingScale[i])
                // Just position it over the player it belongs to
                if (_frostRingPool[i] == null || !_frostRingPool[i].activeSelf) continue;
                // Position is already set when triggered; just let Update animate it
            }
        }

        /// <summary>外部调用：当 FrostNova 触发时生成视觉环。</summary>
        public void TriggerFrostNovaRing(int playerSlot, float radius)
        {
            if (!_initialized) return;
            ref var p = ref _state.Players[playerSlot];
            int ringIdx = playerSlot % MaxFrostRings;
            _frostRingPool[ringIdx].SetActive(true);
            _frostRingPool[ringIdx].transform.position = new Vector3(p.PosX.ToFloat(), 0.05f, p.PosZ.ToFloat());
            _frostRingScale[ringIdx] = radius * 2f; // diameter, decays in Update
            _frostRingPool[ringIdx].transform.localScale = new Vector3(_frostRingScale[ringIdx], 0.05f, _frostRingScale[ringIdx]);
        }

        // ==================== Death Explosions ====================

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

        void UpdateShake()
        {
            if (_shakeIntensity <= 0.001f) { _shakeOffset = Vector3.zero; _shakeIntensity = 0f; return; }
            _shakeOffset = new Vector3(
                (Random.value * 2f - 1f) * _shakeIntensity,
                0f,
                (Random.value * 2f - 1f) * _shakeIntensity);
            _shakeIntensity *= 0.6f;
        }

        // ==================== Damage Numbers ====================

        void UpdateDamageNumbers()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                if (_prevEnemyAlive[i] && e.IsAlive && e.Hp < _prevEnemyHp[i])
                {
                    int dmg = _prevEnemyHp[i] - e.Hp;
                    bool isFocus = _state.FocusFireTarget == i;
                    bool isLightning = IsNearLightningFlash(e.PosX.ToFloat(), e.PosZ.ToFloat());
                    Color col = isFocus ? new Color(1f, 0.3f, 0.1f)
                        : isLightning ? Color.yellow : Color.white;
                    SpawnDamageNumber(_enemyPool[i].transform.position, dmg, col);
                }
            }
            for (int p = 0; p < GameState.MaxPlayers; p++)
            {
                ref var pl = ref _state.Players[p];
                if (!pl.IsActive) continue;
                if (pl.Hp < _prevPlayerHp[p] && _prevPlayerHp[p] > 0)
                    SpawnDamageNumber(_playerObjs[p].transform.position, _prevPlayerHp[p] - pl.Hp, new Color(1f, 0.3f, 0.3f));
                // Heal numbers (green, positive)
                if (pl.Hp > _prevPlayerHp[p] && _prevPlayerHp[p] > 0 && _prevPlayerHp[p] < pl.MaxHp)
                    SpawnDamageNumber(_playerObjs[p].transform.position, pl.Hp - _prevPlayerHp[p], new Color(0.2f, 1f, 0.4f));
            }

            float dt = Time.deltaTime;
            for (int i = 0; i < DmgNumPoolSize; i++)
            {
                ref var d = ref _dmgPool[i];
                if (!d.Active) continue;
                d.TimeLeft -= dt;
                if (d.TimeLeft <= 0f) { d.Active = false; d.Obj.SetActive(false); continue; }
                d.Obj.transform.position += d.Vel * dt;
                d.Obj.transform.rotation = _cam.transform.rotation;
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
                d.TimeLeft = DmgNumDuration; d.Total = DmgNumDuration;
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
                if (dx * dx + dz * dz < 2.25f) return true;
            }
            return false;
        }

        // ==================== Boss Warning ====================

        void UpdateBossWarning()
        {
            if (_state.WaveNumber > 0 && _state.WaveNumber % GameState.BossWaveInterval == 0 && IsNewBossAlive())
                _bossWarningTimer = 2f;
            if (_bossWarningTimer > 0f) _bossWarningTimer -= Time.deltaTime;
        }

        bool IsNewBossAlive()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                var t = _state.Enemies[i].Type;
                if (_state.Enemies[i].IsAlive && (t == EnemyType.Boss || t == EnemyType.TwinCore
                    || t == EnemyType.SplitBoss || t == EnemyType.SplitHalf))
                    return true;
            }
            return false;
        }

        GUIStyle _bossWarnStyle;
        GUIStyle _coopHintStyle;

        void OnGUI()
        {
            if (!_initialized) return;

            // Boss warning
            if (_bossWarningTimer > 0f)
            {
                if (_bossWarnStyle == null)
                    _bossWarnStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 32, fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.red }
                    };
                float alpha = Mathf.Clamp01(_bossWarningTimer);
                var prev = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
                // Show boss type hint
                bool isTwinCore = false;
                for (int i = 0; i < GameState.MaxEnemies; i++)
                    if (_state.Enemies[i].IsAlive && _state.Enemies[i].Type == EnemyType.TwinCore) { isTwinCore = true; break; }
                string bossHint = isTwinCore
                    ? "! TWIN CORE — 双人同时攻击两核！!"
                    : "! SPLIT BOSS — 同时击杀两分裂体！!";
                GUI.Label(new Rect(Screen.width / 2f - 220f, Screen.height * 0.15f, 440f, 60f),
                    bossHint, _bossWarnStyle);
                GUI.color = prev;
            }

            // TwinCore hint: show hit window status
            if (_coopHintStyle == null)
                _coopHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16, alignment = TextAnchor.UpperLeft,
                    normal = { textColor = new Color(0.8f, 0.9f, 1f) }
                };
            bool anyTwinCore = false;
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                if (!e.IsAlive || e.Type != EnemyType.TwinCore) continue;
                if (e.HitWindowTimer > 0 && !anyTwinCore)
                {
                    anyTwinCore = true;
                    GUI.Label(new Rect(10f, Screen.height - 80f, 300f, 30f),
                        $"核心已标记！快击中另一核！({e.HitWindowTimer}帧)", _coopHintStyle);
                }
            }
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
            if (_jobsInitialized)
            {
                _enemyTransformArray.Dispose();
                _enemyJobData.Dispose();
                _enemyJobAlive.Dispose();
            }

            Destroy(_matPlayer); Destroy(_matPlayerHit);
            Destroy(_matZombie); Destroy(_matBat); Destroy(_matMage); Destroy(_matBoss);
            Destroy(_matKnife); Destroy(_matBoneShard);
            Destroy(_matGem); Destroy(_matGround);
            Destroy(_matOrb); Destroy(_matLightning); Destroy(_matHolyWater);
            Destroy(_matEnemyFlash);
            Destroy(_matLinkBeam); Destroy(_matHealAura); Destroy(_matShieldWall);
            Destroy(_matFocusFireMark); Destroy(_matRevivalTotem); Destroy(_matFrostNova);
            Destroy(_matFireTrail); Destroy(_matTwinCoreA); Destroy(_matTwinCoreB);
            Destroy(_matTwinCoreHit); Destroy(_matSplitBoss); Destroy(_matSplitHalf);
            Destroy(_matSplitShotMain); Destroy(_matSplitShotSplinter);
            for (int i = 0; i < _playerMats.Length; i++) if (_playerMats[i] != null) Destroy(_playerMats[i]);
            for (int i = 0; i < _orbMats.Length; i++) if (_orbMats[i] != null) Destroy(_orbMats[i]);
            if (_focusFireMarkMat != null) Destroy(_focusFireMarkMat);
        }
    }
}
