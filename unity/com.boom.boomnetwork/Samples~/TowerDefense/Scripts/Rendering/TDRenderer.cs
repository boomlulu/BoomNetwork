// BoomNetwork TowerDefense Demo — Renderer (Visual Upgrade)
//
// 摄像机：70° 斜视角，正交，可见塔高和 3D 感
// 塔外形：弓箭塔=细高圆柱 / 炮台=宽矮圆柱+球 / 魔法塔=胶囊+顶球
// 敌人：Basic=方块 / Fast=旋转45°菱形 / Tank=宽圆柱；HP 越低越缩小
// 基地：金色光标脉动
// 攻击特效：独立三池（Arrow/Cannon/Magic）

using UnityEngine;

namespace BoomNetwork.Samples.TowerDefense
{
    public class TDRenderer : MonoBehaviour
    {
        GameState _state;
        bool _initialized;
        Camera _cam;

        // ==================== Materials ====================
        Material _matGround, _matGridEven, _matGridOdd, _matBase, _matBaseBeacon;
        // Tower materials: [TowerType 1-7][level 1-3] — pre-tinted per level
        Material[,] _matTowerBase = new Material[8, 4];
        Material[,] _matTowerDeco = new Material[8, 4];
        // Owner indicator materials: [slot 0-3 + Team(4)]
        Material[] _matOwner = new Material[5];
        // Owner indicator sphere per grid cell
        GameObject[] _ownerIndicator     = new GameObject[GameState.GridSize];
        Renderer[]   _ownerIndicatorRend = new Renderer[GameState.GridSize];
        Material _matBasic, _matFast, _matTank, _matArmored, _matElite, _matSlowed;
        Material _matFxArrow, _matFxBall, _matFxExplosion, _matFxMagic;
        // ==================== Cell highlight ====================
        GameObject _cellHighlight;
        Renderer   _cellHighlightRend;
        Material   _matCellHighlight;

        // ==================== World-space IMGUI bars ====================
        Texture2D _barTex; // 1×1 white, tinted via GUI.color

        // ==================== Tower pool ====================
        // 每格：底座 + 顶部装饰（各有自己的 Renderer）
        GameObject[] _towerBase = new GameObject[GameState.GridSize];
        GameObject[] _towerDeco = new GameObject[GameState.GridSize]; // 炮球/魔法顶球/箭头
        Renderer[]   _towerBaseRend = new Renderer[GameState.GridSize];
        Renderer[]   _towerDecoRend = new Renderer[GameState.GridSize];

        // 塔攻击"后坐"缩放（纯视觉）
        float[] _towerPunchTimer = new float[GameState.GridSize];
        int[]   _prevCooldown    = new int[GameState.GridSize];

        // ==================== Enemy pool ====================
        GameObject[] _enemyObjs = new GameObject[GameState.MaxEnemies];
        Renderer[]   _enemyRend = new Renderer[GameState.MaxEnemies];
        int[]        _prevEnemyHp = new int[GameState.MaxEnemies]; // HP 变化检测

        // ==================== Base beacon ====================
        GameObject _baseBeacon;
        Renderer   _baseBeaconRend;

        // ==================== Arrow FX ====================
        const int ArrowFxPool = 24;
        struct ArrowFx { public bool Active; public Vector3 Origin, Target; public int FramesLeft; public GameObject Obj; }
        ArrowFx[] _arrowFx = new ArrowFx[ArrowFxPool];

        // ==================== Cannon FX ====================
        const int CannonFxPool = 12;
        const int CannonTravelFrames    = 15;
        const int CannonExplosionFrames = 10;
        struct CannonFx
        {
            public bool Active, Exploded;
            public Vector3 Origin, Target;
            public int FramesTotal, FramesLeft;
            public GameObject Ball, Explosion;
        }
        CannonFx[] _cannonFx = new CannonFx[CannonFxPool];

        // ==================== Magic FX ====================
        const int MagicFxPool = 16;
        struct MagicFx { public bool Active; public Vector3 Target; public int FramesTotal, FramesLeft; public GameObject Obj; }
        MagicFx[] _magicFx = new MagicFx[MagicFxPool];

        // ==================== Ice FX (expanding ring) ====================
        const int IceFxPool = 12;
        struct IceFx { public bool Active; public Vector3 Center; public int FramesTotal, FramesLeft; public float MaxScale; public GameObject Obj; }
        IceFx[] _iceFx = new IceFx[IceFxPool];
        Material _matFxIce;

        // ==================== Sniper FX (instant beam) ====================
        const int SniperFxPool = 8;
        struct SniperFx { public bool Active; public Vector3 Origin, Target; public int FramesLeft; public GameObject Obj; }
        SniperFx[] _sniperFx = new SniperFx[SniperFxPool];
        Material _matFxSniper;

        // ==================== Fortress FX (heavy explosion) ====================
        const int FortressFxPool = 6;
        const int FortressTravelFrames    = 12;
        const int FortressExplosionFrames = 14;
        CannonFx[] _fortressFx = new CannonFx[FortressFxPool]; // reuse CannonFx struct
        Material _matFxFortressBall, _matFxFortressExplosion;

        // ==================== Storm FX (electric arc) ====================
        const int StormFxPool = 16;
        struct StormFx { public bool Active; public Vector3 Origin, Target; public int FramesLeft; public GameObject Obj; }
        StormFx[] _stormFx = new StormFx[StormFxPool];
        Material _matFxStorm;

        // ==================== Camera ====================
        // 70° 仰角斜视，能看到塔高度
        const float CamTiltX   = 70f;
        const float CamHeight  = 26f;
        const float CamOrtho   = 13f;
        static readonly Vector3 MapCenter = new Vector3(GameState.GridW * 0.5f, 0f, GameState.GridH * 0.5f);

        // ==================== Init ====================

        public void Init(GameState state)
        {
            _state = state;
            if (_initialized) return;
            _initialized = true;

            CreateMaterials();
            CreateCamera();
            CreateGround();
            CreateBase();
            CreateTowerPool();
            CreateOwnerIndicatorPool();
            CreateEnemyPool();
            CreateArrowFxPool();
            CreateCannonFxPool();
            CreateMagicFxPool();
            CreateIceFxPool();
            CreateSniperFxPool();
            CreateFortressFxPool();
            CreateStormFxPool();
            CreateCellHighlight();

            for (int i = 0; i < GameState.GridSize; i++) _prevCooldown[i] = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++) _prevEnemyHp[i] = 0;
        }

        // ==================== SyncVisuals ====================

        public void SyncVisuals()
        {
            if (!_initialized || _state == null) return;
            SyncTowers();
            SyncEnemies();
            TickArrowFx();
            TickCannonFx();
            TickMagicFx();
            TickIceFx();
            TickSniperFx();
            TickFortressFx();
            TickStormFx();
            CaptureFrameShadow();
        }

        // ==================== Update（纯视觉动画） ====================

        void Update()
        {
            if (!_initialized || _state == null) return;
            AnimateBase();
            AnimateTowerPunch();
        }

        void AnimateBase()
        {
            if (_baseBeacon == null) return;
            float hp = _state.BaseHp;
            // 脉动：基地血量越低，脉动越快
            float speed = 1.5f + (3 - hp) * 1.2f;
            float pulse = Mathf.Sin(Time.time * speed) * 0.5f + 0.5f; // 0→1
            float s = Mathf.Lerp(0.6f, 1.1f, pulse);
            _baseBeacon.transform.localScale = new Vector3(s, s * 0.3f, s);
            // 颜色随血量：满血金色 → 半血橙色 → 低血红色
            Color baseColor = hp >= 3
                ? Color.Lerp(new Color(0.9f, 0.7f, 0.1f), new Color(1f, 0.9f, 0.3f), pulse)
                : hp == 2
                    ? Color.Lerp(new Color(0.9f, 0.4f, 0.1f), new Color(1f, 0.6f, 0.1f), pulse)
                    : Color.Lerp(new Color(0.8f, 0.1f, 0.1f), new Color(1f, 0.2f, 0.2f), pulse);
            _baseBeaconRend.material.color = baseColor;
        }

        void AnimateTowerPunch()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < GameState.GridSize; i++)
            {
                if (_towerPunchTimer[i] <= 0f) continue;
                _towerPunchTimer[i] -= dt * 12f; // 快速衰减
                if (_towerPunchTimer[i] < 0f) _towerPunchTimer[i] = 0f;
                // 后坐：先缩再弹回
                float punch = Mathf.Sin(_towerPunchTimer[i] * Mathf.PI);
                float s = 1f + punch * 0.25f;
                if (_towerBase[i].activeSelf)
                {
                    ref var gt = ref _state.Grid[i];
                    _towerBase[i].transform.localScale = GetTowerBaseScale(gt.Type, gt.Level) * s;
                }
            }
        }

        // ==================== SyncTowers ====================

        void SyncTowers()
        {
            for (int i = 0; i < GameState.GridSize; i++)
            {
                ref var t = ref _state.Grid[i];
                bool show = t.Type != TowerType.None;
                _towerBase[i].SetActive(show);
                _towerDeco[i].SetActive(show);

                // Owner indicator: visible only when tower present
                _ownerIndicator[i].SetActive(show);
                if (show)
                {
                    int ownerIdx = t.OwnerId == GameState.TeamOwner ? 4 :
                                   (t.OwnerId >= 0 && t.OwnerId < GameState.MaxPlayers ? t.OwnerId : 4);
                    _ownerIndicatorRend[i].sharedMaterial = _matOwner[ownerIdx];
                }

                if (!show) continue;

                // Scale by level (punch animation may override momentarily)
                if (_towerPunchTimer[i] <= 0f)
                    _towerBase[i].transform.localScale = GetTowerBaseScale(t.Type, t.Level);

                int lv = Mathf.Clamp(t.Level, 1, 3);
                int ti = Mathf.Clamp((int)t.Type, 1, 7);
                _towerBaseRend[i].sharedMaterial = _matTowerBase[ti, lv];
                _towerDecoRend[i].sharedMaterial = _matTowerDeco[ti, lv];

                // 开火检测
                int maxCd = GameState.GetTowerCooldown(t.Type, t.Level);
                if (t.CooldownFrames == maxCd && _prevCooldown[i] < maxCd)
                {
                    _towerPunchTimer[i] = 1f; // 触发后坐动画
                    int cx = i % GameState.GridW;
                    int cy = i / GameState.GridW;
                    Vector3 origin = new Vector3(cx + 0.5f, 1.0f, cy + 0.5f);
                    Vector3 target = FindNearestEnemyPos(origin, GameState.GetTowerRange(t.Type, t.Level).ToFloat());
                    SpawnFx(t.Type, origin, target);
                }
            }
        }

        Vector3 GetTowerBaseScale(TowerType type, int level = 1)
        {
            float s = 1f + (level - 1) * 0.22f;
            switch (type)
            {
                case TowerType.Arrow:    return new Vector3(0.32f * s, 1.30f * s, 0.32f * s); // 细高
                case TowerType.Cannon:   return new Vector3(0.75f * s, 0.45f * s, 0.75f * s); // 宽矮
                case TowerType.Magic:    return new Vector3(0.38f * s, 0.95f * s, 0.38f * s); // 中等
                case TowerType.Ice:      return new Vector3(0.42f * s, 0.80f * s, 0.42f * s); // 粗短冰柱
                case TowerType.Sniper:   return new Vector3(0.22f * s, 1.70f * s, 0.22f * s); // 极细高
                case TowerType.Fortress: return new Vector3(0.90f * s, 0.60f * s, 0.90f * s); // 宽重炮座
                case TowerType.Storm:    return new Vector3(0.28f * s, 1.50f * s, 0.28f * s); // 高细电塔
                default:                 return new Vector3(0.35f * s, 1.00f * s, 0.35f * s);
            }
        }

        Vector3 FindNearestEnemyPos(Vector3 towerPos, float range)
        {
            float rangeSq  = range * range;
            float bestDist = float.MaxValue;
            Vector3 best   = towerPos;
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                if (!e.IsAlive) continue;
                float ex = e.PosX.ToFloat(), ez = e.PosZ.ToFloat();
                float dx = ex - towerPos.x, dz = ez - towerPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= rangeSq && dSq < bestDist)
                {
                    bestDist = dSq;
                    best = new Vector3(ex, 0.3f, ez);
                }
            }
            return best;
        }

        // ==================== SyncEnemies ====================

        void SyncEnemies()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                bool show = e.IsAlive;
                _enemyObjs[i].SetActive(show);
                if (!show) continue;

                float x = e.PosX.ToFloat(), z = e.PosZ.ToFloat();

                // HP 比例决定尺寸（血越少越小，有濒死感）
                int maxHp = GameState.GetEnemyHp(e.Type);
                float hpRatio = Mathf.Clamp01((float)e.Hp / maxHp);
                float shrink = Mathf.Lerp(0.55f, 1.0f, hpRatio);

                bool slowed = e.SlowFrames > 0;

                switch (e.Type)
                {
                    case EnemyType.Basic:
                        _enemyRend[i].sharedMaterial = slowed ? _matSlowed : _matBasic;
                        _enemyObjs[i].transform.position   = new Vector3(x, 0.25f * shrink, z);
                        _enemyObjs[i].transform.localScale  = new Vector3(0.5f * shrink, 0.5f * shrink, 0.5f * shrink);
                        break;
                    case EnemyType.Fast:
                        _enemyRend[i].sharedMaterial = slowed ? _matSlowed : _matFast;
                        _enemyObjs[i].transform.rotation    = Quaternion.Euler(0f, 45f, 0f);
                        _enemyObjs[i].transform.position    = new Vector3(x, 0.2f * shrink, z);
                        _enemyObjs[i].transform.localScale  = new Vector3(0.45f * shrink, 0.35f * shrink, 0.45f * shrink);
                        break;
                    case EnemyType.Tank:
                        _enemyRend[i].sharedMaterial = slowed ? _matSlowed : _matTank;
                        _enemyObjs[i].transform.position   = new Vector3(x, 0.2f * shrink, z);
                        _enemyObjs[i].transform.localScale  = new Vector3(0.8f * shrink, 0.35f, 0.8f * shrink);
                        break;
                    case EnemyType.Armored: // 宽圆柱，灰色，高HP所以缩放不明显
                        _enemyRend[i].sharedMaterial = slowed ? _matSlowed : _matArmored;
                        _enemyObjs[i].transform.position   = new Vector3(x, 0.25f * shrink, z);
                        _enemyObjs[i].transform.localScale  = new Vector3(0.75f * shrink, 0.55f * shrink, 0.75f * shrink);
                        break;
                    case EnemyType.Elite: // 细高金色方块，快速移动
                        _enemyRend[i].sharedMaterial = _matElite; // 不受减速色影响（免疫）
                        _enemyObjs[i].transform.position   = new Vector3(x, 0.3f * shrink, z);
                        _enemyObjs[i].transform.localScale  = new Vector3(0.38f * shrink, 0.6f * shrink, 0.38f * shrink);
                        break;
                }
            }
        }

        void SpawnFx(TowerType type, Vector3 origin, Vector3 target)
        {
            switch (type)
            {
                case TowerType.Arrow:    SpawnArrow(origin, target);    break;
                case TowerType.Cannon:   SpawnCannon(origin, target);   break;
                case TowerType.Magic:    SpawnMagic(target);            break;
                case TowerType.Ice:      SpawnIce(origin);              break;
                case TowerType.Sniper:   SpawnSniper(origin, target);   break;
                case TowerType.Fortress: SpawnFortress(origin, target); break;
                case TowerType.Storm:    SpawnStorm(origin, target);    break;
            }
        }

        // ==================== Arrow FX ====================

        void SpawnArrow(Vector3 origin, Vector3 target)
        {
            for (int i = 0; i < ArrowFxPool; i++)
            {
                ref var f = ref _arrowFx[i];
                if (f.Active) continue;
                f.Active = true; f.Origin = origin; f.Target = target; f.FramesLeft = 8;
                f.Obj.SetActive(true);
                return;
            }
        }

        void TickArrowFx()
        {
            for (int i = 0; i < ArrowFxPool; i++)
            {
                ref var f = ref _arrowFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (f.FramesLeft <= 0) { f.Active = false; f.Obj.SetActive(false); continue; }

                float t   = 1f - f.FramesLeft / 8f;
                Vector3 pos = Vector3.Lerp(f.Origin, f.Target, t);
                f.Obj.transform.position = pos;
                Vector3 dir = f.Target - f.Origin;
                if (dir.sqrMagnitude > 0.001f)
                    f.Obj.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                float len = Mathf.Max(0.2f, dir.magnitude * 0.4f);
                f.Obj.transform.localScale = new Vector3(0.1f, 0.1f, len);
            }
        }

        // ==================== Cannon FX ====================

        void SpawnCannon(Vector3 origin, Vector3 target)
        {
            for (int i = 0; i < CannonFxPool; i++)
            {
                ref var f = ref _cannonFx[i];
                if (f.Active) continue;
                f.Active = true; f.Exploded = false;
                f.Origin = origin; f.Target = target;
                f.FramesTotal = CannonTravelFrames; f.FramesLeft = CannonTravelFrames;
                f.Ball.transform.position   = new Vector3(origin.x, 1.5f, origin.z);
                f.Ball.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
                f.Ball.SetActive(true);
                f.Explosion.SetActive(false);
                return;
            }
        }

        void TickCannonFx()
        {
            for (int i = 0; i < CannonFxPool; i++)
            {
                ref var f = ref _cannonFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;

                if (!f.Exploded)
                {
                    float t = 1f - (float)f.FramesLeft / f.FramesTotal;
                    float px = Mathf.Lerp(f.Origin.x, f.Target.x, t);
                    float pz = Mathf.Lerp(f.Origin.z, f.Target.z, t);
                    float arcY = Mathf.Lerp(1.5f, 0.5f, t) + Mathf.Sin(t * Mathf.PI) * 1.5f;
                    f.Ball.transform.position   = new Vector3(px, arcY, pz);
                    float s = Mathf.Lerp(0.35f, 0.75f, t);
                    f.Ball.transform.localScale = new Vector3(s, s, s);

                    if (f.FramesLeft <= 0)
                    {
                        f.Exploded = true;
                        f.FramesTotal = CannonExplosionFrames; f.FramesLeft = CannonExplosionFrames;
                        f.Ball.SetActive(false);
                        f.Explosion.transform.position   = new Vector3(f.Target.x, 0.5f, f.Target.z);
                        f.Explosion.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                        f.Explosion.SetActive(true);
                    }
                }
                else
                {
                    float t = 1f - (float)f.FramesLeft / f.FramesTotal;
                    float s = t < 0.4f
                        ? Mathf.Lerp(0.5f, 4.5f, t / 0.4f)
                        : Mathf.Lerp(4.5f, 0.05f, (t - 0.4f) / 0.6f);
                    f.Explosion.transform.localScale = new Vector3(s, s * 0.3f, s);
                    if (f.FramesLeft <= 0) { f.Active = false; f.Explosion.SetActive(false); }
                }
            }
        }

        // ==================== Magic FX ====================

        void SpawnMagic(Vector3 target)
        {
            for (int i = 0; i < MagicFxPool; i++)
            {
                ref var f = ref _magicFx[i];
                if (f.Active) continue;
                f.Active = true; f.Target = target; f.FramesTotal = 12; f.FramesLeft = 12;
                f.Obj.transform.position = target;
                f.Obj.SetActive(true);
                return;
            }
        }

        void TickMagicFx()
        {
            for (int i = 0; i < MagicFxPool; i++)
            {
                ref var f = ref _magicFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (f.FramesLeft <= 0) { f.Active = false; f.Obj.SetActive(false); continue; }
                float t     = 1f - (float)f.FramesLeft / f.FramesTotal;
                float pulse = Mathf.Sin(t * Mathf.PI);
                float s     = 0.15f + pulse * 1.2f;
                f.Obj.transform.localScale = new Vector3(s, s * 0.25f, s);
            }
        }

        // ==================== Ice FX (expanding ring) ====================

        void SpawnIce(Vector3 center)
        {
            for (int i = 0; i < IceFxPool; i++)
            {
                ref var f = ref _iceFx[i];
                if (f.Active) continue;
                f.Active = true; f.Center = center; f.FramesTotal = 16; f.FramesLeft = 16;
                f.MaxScale = 5f;
                f.Obj.transform.position = new Vector3(center.x, 0.08f, center.z);
                f.Obj.SetActive(true);
                return;
            }
        }

        void TickIceFx()
        {
            for (int i = 0; i < IceFxPool; i++)
            {
                ref var f = ref _iceFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (f.FramesLeft <= 0) { f.Active = false; f.Obj.SetActive(false); continue; }
                float t = 1f - (float)f.FramesLeft / f.FramesTotal;
                float s = Mathf.Lerp(0.3f, f.MaxScale, t);
                float a = 1f - t; // fade out
                f.Obj.transform.localScale = new Vector3(s, 0.08f, s);
                _matFxIce.color = new Color(0.5f, 0.9f, 1f, a * 0.7f);
            }
        }

        // ==================== Sniper FX (instant beam) ====================

        void SpawnSniper(Vector3 origin, Vector3 target)
        {
            for (int i = 0; i < SniperFxPool; i++)
            {
                ref var f = ref _sniperFx[i];
                if (f.Active) continue;
                f.Active = true; f.Origin = origin; f.Target = target; f.FramesLeft = 5;
                f.Obj.SetActive(true);
                return;
            }
        }

        void TickSniperFx()
        {
            for (int i = 0; i < SniperFxPool; i++)
            {
                ref var f = ref _sniperFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (f.FramesLeft <= 0) { f.Active = false; f.Obj.SetActive(false); continue; }
                // Thin line from origin to target
                Vector3 mid = (f.Origin + f.Target) * 0.5f;
                Vector3 dir = f.Target - f.Origin;
                float len = dir.magnitude;
                f.Obj.transform.position   = mid;
                f.Obj.transform.localScale = new Vector3(0.07f, 0.07f, len);
                if (len > 0.01f) f.Obj.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
        }

        // ==================== Shadow Copy ====================

        void CaptureFrameShadow()
        {
            for (int i = 0; i < GameState.GridSize; i++)   _prevCooldown[i]  = _state.Grid[i].CooldownFrames;
            for (int i = 0; i < GameState.MaxEnemies; i++) _prevEnemyHp[i]   = _state.Enemies[i].Hp;
        }

        // ==================== Mouse → Grid Cell ====================

        public bool TryGetGridCell(Vector2 screenPos, out int gx, out int gy)
        {
            gx = gy = -1;
            if (_cam == null) return false;
            Ray ray = _cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
            if (Mathf.Abs(ray.direction.y) < 0.0001f) return false;
            float t = -ray.origin.y / ray.direction.y;
            if (t < 0f) return false;
            Vector3 hit = ray.origin + t * ray.direction;
            gx = Mathf.FloorToInt(hit.x);
            gy = Mathf.FloorToInt(hit.z);
            if (!GameState.IsInBounds(gx, gy)) { gx = gy = -1; return false; }
            return true;
        }

        public void SetCellHighlight(int gx, int gy)
        {
            if (_cellHighlight == null) return;
            if (gx < 0 || !GameState.IsInBounds(gx, gy))
            {
                _cellHighlight.SetActive(false);
                return;
            }
            _cellHighlight.transform.position = new Vector3(gx + 0.5f, 0.03f, gy + 0.5f);
            _cellHighlight.SetActive(true);
        }

        public Vector2 GetCellScreenCenter(int gx, int gy)
        {
            if (_cam == null) return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector3 world  = new Vector3(gx + 0.5f, 0f, gy + 0.5f);
            Vector3 screen = _cam.WorldToScreenPoint(world);
            return new Vector2(screen.x, screen.y);
        }

        // ==================== Object Creation ====================

        void CreateMaterials()
        {
            // 优先 URP Lit，回退 Standard
            var sh  = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var unl = Shader.Find("Universal Render Pipeline/Unlit") ?? sh;

            // 地面与格子（棋盘格增强层次感）
            _matGround   = new Material(sh)  { color = new Color(0.10f, 0.11f, 0.14f) };
            _matGridEven = new Material(unl) { color = new Color(0.14f, 0.16f, 0.20f) };
            _matGridOdd  = new Material(unl) { color = new Color(0.17f, 0.19f, 0.24f) };

            // 基地
            _matBase       = new Material(sh) { color = new Color(0.80f, 0.60f, 0.10f) };
            _matBaseBeacon = new Material(sh) { color = new Color(1.00f, 0.85f, 0.20f) };

            // 塔：预生成 3 个等级的颜色（每级亮 25%）— 7 tower types
            Color[] baseColors = { Color.clear,
                new Color(0.15f, 0.75f, 0.30f),  // Arrow   — bright green
                new Color(0.75f, 0.35f, 0.05f),  // Cannon  — deep orange
                new Color(0.45f, 0.15f, 0.85f),  // Magic   — deep purple
                new Color(0.10f, 0.55f, 0.90f),  // Ice     — sky blue
                new Color(0.50f, 0.35f, 0.10f),  // Sniper  — dark bronze
                new Color(0.40f, 0.10f, 0.10f),  // Fortress— dark crimson
                new Color(0.10f, 0.20f, 0.50f),  // Storm   — deep navy
            };
            Color[] decoColors = { Color.clear,
                new Color(0.80f, 1.00f, 0.60f),  // Arrow deco   — light green
                new Color(0.25f, 0.25f, 0.30f),  // Cannon deco  — steel grey
                new Color(0.85f, 0.55f, 1.00f),  // Magic deco   — bright violet
                new Color(0.70f, 0.95f, 1.00f),  // Ice deco     — icy white-blue
                new Color(0.90f, 0.80f, 0.30f),  // Sniper deco  — gold lens
                new Color(0.90f, 0.20f, 0.20f),  // Fortress deco— bright red dome
                new Color(0.40f, 0.80f, 1.00f),  // Storm deco   — electric blue tip
            };
            for (int ti = 1; ti <= 7; ti++)
            {
                for (int lv = 1; lv <= 3; lv++)
                {
                    float t = 1f + (lv - 1) * 0.25f;
                    _matTowerBase[ti, lv] = new Material(sh) { color = baseColors[ti] * t };
                    _matTowerDeco[ti, lv] = new Material(sh) { color = decoColors[ti] * t };
                }
            }

            // Owner indicators: P0=white P1=sky-blue P2=orange P3=purple Team=gold
            _matOwner[0] = new Material(sh) { color = new Color(1.00f, 1.00f, 1.00f) };
            _matOwner[1] = new Material(sh) { color = new Color(0.30f, 0.70f, 1.00f) };
            _matOwner[2] = new Material(sh) { color = new Color(1.00f, 0.55f, 0.15f) };
            _matOwner[3] = new Material(sh) { color = new Color(0.80f, 0.30f, 1.00f) };
            _matOwner[4] = new Material(sh) { color = new Color(1.00f, 0.85f, 0.10f) }; // team=gold

            // 敌人
            _matBasic   = new Material(sh) { color = new Color(0.90f, 0.15f, 0.15f) }; // 红
            _matFast    = new Material(sh) { color = new Color(1.00f, 0.55f, 0.05f) }; // 橙
            _matTank    = new Material(sh) { color = new Color(0.40f, 0.05f, 0.05f) }; // 暗红
            _matArmored = new Material(sh) { color = new Color(0.60f, 0.63f, 0.68f) }; // 铠甲灰
            _matElite   = new Material(sh) { color = new Color(0.95f, 0.80f, 0.05f) }; // 精英金
            _matSlowed  = new Material(sh) { color = new Color(0.35f, 0.35f, 0.90f) }; // 减速蓝

            // FX
            _matFxArrow     = new Material(sh) { color = new Color(1.00f, 0.95f, 0.30f) };
            _matFxBall      = new Material(sh) { color = new Color(1.00f, 0.85f, 0.10f) };
            _matFxExplosion = new Material(sh) { color = new Color(1.00f, 0.35f, 0.00f) };
            _matFxMagic     = new Material(sh) { color = new Color(0.80f, 0.30f, 1.00f) };
            _matFxIce       = new Material(unl) { color = new Color(0.50f, 0.90f, 1.00f, 0.7f) };
            _matFxSniper    = new Material(sh)  { color = new Color(0.95f, 0.90f, 0.20f) };
            _matFxFortressBall       = new Material(sh) { color = new Color(0.20f, 0.05f, 0.05f) };
            _matFxFortressExplosion  = new Material(sh) { color = new Color(0.85f, 0.10f, 0.10f) };
            _matFxStorm              = new Material(sh) { color = new Color(0.55f, 0.90f, 1.00f) };
        }

        void CreateCamera()
        {
            var camObj = new GameObject("TD_Camera");
            camObj.transform.SetParent(transform);
            _cam = camObj.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.04f, 0.04f, 0.06f);
            _cam.orthographic = true;
            _cam.orthographicSize = CamOrtho;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 80f;

            // 70° 斜视角：摄像机在地图中心正上方偏后方
            float tiltRad = CamTiltX * Mathf.Deg2Rad;
            float camY    = CamHeight;
            float camBack = camY / Mathf.Tan(tiltRad); // 向后退以对准地图
            camObj.transform.position = new Vector3(MapCenter.x, camY, MapCenter.z - camBack);
            camObj.transform.rotation = Quaternion.Euler(CamTiltX, 0f, 0f);

            var mainCam = Camera.main;
            if (mainCam != null && mainCam != _cam) mainCam.gameObject.SetActive(false);
        }

        void CreateGround()
        {
            // 棋盘格：每格单独 Quad
            for (int cy = 0; cy < GameState.GridH; cy++)
            {
                for (int cx = 0; cx < GameState.GridW; cx++)
                {
                    var cell = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    cell.name = "TD_Cell";
                    cell.transform.SetParent(transform);
                    cell.transform.position = new Vector3(cx + 0.5f, 0f, cy + 0.5f);
                    cell.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    cell.transform.localScale = new Vector3(0.98f, 0.98f, 1f);
                    bool even = (cx + cy) % 2 == 0;
                    cell.GetComponent<Renderer>().sharedMaterial = even ? _matGridEven : _matGridOdd;
                    DestroyCollider(cell);
                }
            }
        }

        void CreateBase()
        {
            // 2×2 基地底板
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int cx = GameState.BaseCX + dx, cy = GameState.BaseCY + dy;
                    var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    obj.name = "TD_BaseTile";
                    obj.transform.SetParent(transform);
                    obj.transform.position  = new Vector3(cx + 0.5f, 0.01f, cy + 0.5f);
                    obj.transform.rotation  = Quaternion.Euler(90f, 0f, 0f);
                    obj.transform.localScale = new Vector3(0.97f, 0.97f, 1f);
                    obj.GetComponent<Renderer>().sharedMaterial = _matBase;
                    DestroyCollider(obj);
                }
            }

            // 中心脉动光标（Sphere 扁平化）
            _baseBeacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _baseBeacon.name = "TD_BaseBeacon";
            _baseBeacon.transform.SetParent(transform);
            _baseBeacon.transform.position   = new Vector3(GameState.BaseCX + 1.0f, 0.2f, GameState.BaseCY + 1.0f);
            _baseBeacon.transform.localScale  = new Vector3(0.8f, 0.2f, 0.8f);
            _baseBeaconRend = _baseBeacon.GetComponent<Renderer>();
            _baseBeaconRend.material = new Material(_matBaseBeacon);
            DestroyCollider(_baseBeacon);
        }

        void CreateTowerPool()
        {
            for (int i = 0; i < GameState.GridSize; i++)
            {
                int cx = i % GameState.GridW, cy = i / GameState.GridW;
                float wx = cx + 0.5f, wz = cy + 0.5f;

                // 底座（弓箭=Cylinder，炮台=Cylinder，魔法=Cylinder）
                var tBase = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                tBase.name = "TD_TowerBase";
                tBase.transform.SetParent(transform);
                tBase.transform.position   = new Vector3(wx, 0.4f, wz);
                tBase.transform.localScale = new Vector3(0.35f, 0.5f, 0.35f);
                _towerBaseRend[i] = tBase.GetComponent<Renderer>();
                _towerBaseRend[i].sharedMaterial = _matTowerBase[1, 1];
                DestroyCollider(tBase);
                tBase.SetActive(false);
                _towerBase[i] = tBase;

                // 顶部装饰（弓箭=Cube尖顶，炮台=Sphere炮球，魔法=Sphere水晶球）
                var tDeco = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tDeco.name = "TD_TowerDeco";
                tDeco.transform.SetParent(transform);
                tDeco.transform.position   = new Vector3(wx, 1.1f, wz);
                tDeco.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                _towerDecoRend[i] = tDeco.GetComponent<Renderer>();
                _towerDecoRend[i].sharedMaterial = _matTowerDeco[1, 1];
                DestroyCollider(tDeco);
                tDeco.SetActive(false);
                _towerDeco[i] = tDeco;
            }
        }

        void CreateEnemyPool()
        {
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                // Basic/Tank 用 Cube，Fast 用 Cube（旋转 45°）
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "TD_Enemy";
                obj.transform.SetParent(transform);
                obj.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                _enemyRend[i] = obj.GetComponent<Renderer>();
                _enemyRend[i].sharedMaterial = _matBasic;
                DestroyCollider(obj);
                obj.SetActive(false);
                _enemyObjs[i] = obj;
            }
        }

        void CreateArrowFxPool()
        {
            for (int i = 0; i < ArrowFxPool; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "TD_ArrowFx";
                obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matFxArrow;
                DestroyCollider(obj);
                obj.SetActive(false);
                _arrowFx[i] = new ArrowFx { Obj = obj };
            }
        }

        void CreateCannonFxPool()
        {
            for (int i = 0; i < CannonFxPool; i++)
            {
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "TD_CannonBall";
                ball.transform.SetParent(transform);
                ball.GetComponent<Renderer>().sharedMaterial = _matFxBall;
                DestroyCollider(ball);
                ball.SetActive(false);

                var expl = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                expl.name = "TD_CannonExpl";
                expl.transform.SetParent(transform);
                expl.GetComponent<Renderer>().sharedMaterial = _matFxExplosion;
                DestroyCollider(expl);
                expl.SetActive(false);

                _cannonFx[i] = new CannonFx { Ball = ball, Explosion = expl };
            }
        }

        void CreateMagicFxPool()
        {
            for (int i = 0; i < MagicFxPool; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "TD_MagicFx";
                obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matFxMagic;
                DestroyCollider(obj);
                obj.SetActive(false);
                _magicFx[i] = new MagicFx { Obj = obj };
            }
        }

        void CreateIceFxPool()
        {
            for (int i = 0; i < IceFxPool; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                obj.name = "TD_IceFx";
                obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().material = _matFxIce;
                DestroyCollider(obj);
                obj.SetActive(false);
                _iceFx[i] = new IceFx { Obj = obj };
            }
        }

        void CreateSniperFxPool()
        {
            for (int i = 0; i < SniperFxPool; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "TD_SniperFx";
                obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matFxSniper;
                DestroyCollider(obj);
                obj.SetActive(false);
                _sniperFx[i] = new SniperFx { Obj = obj };
            }
        }

        void CreateOwnerIndicatorPool()
        {
            for (int i = 0; i < GameState.GridSize; i++)
            {
                int cx = i % GameState.GridW, cy = i / GameState.GridW;
                var obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                obj.name = "TD_OwnerDot";
                obj.transform.SetParent(transform);
                obj.transform.position   = new Vector3(cx + 0.5f, 1.8f, cy + 0.5f);
                obj.transform.localScale = new Vector3(0.18f, 0.18f, 0.18f);
                _ownerIndicatorRend[i] = obj.GetComponent<Renderer>();
                _ownerIndicatorRend[i].sharedMaterial = _matOwner[4]; // default team gold
                DestroyCollider(obj);
                obj.SetActive(false);
                _ownerIndicator[i] = obj;
            }
        }

        void CreateFortressFxPool()
        {
            for (int i = 0; i < FortressFxPool; i++)
            {
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "TD_FortressBall";
                ball.transform.SetParent(transform);
                ball.GetComponent<Renderer>().sharedMaterial = _matFxFortressBall;
                DestroyCollider(ball);
                ball.SetActive(false);

                var expl = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                expl.name = "TD_FortressExpl";
                expl.transform.SetParent(transform);
                expl.GetComponent<Renderer>().sharedMaterial = _matFxFortressExplosion;
                DestroyCollider(expl);
                expl.SetActive(false);

                _fortressFx[i] = new CannonFx { Ball = ball, Explosion = expl };
            }
        }

        void CreateStormFxPool()
        {
            for (int i = 0; i < StormFxPool; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "TD_StormFx";
                obj.transform.SetParent(transform);
                obj.GetComponent<Renderer>().sharedMaterial = _matFxStorm;
                DestroyCollider(obj);
                obj.SetActive(false);
                _stormFx[i] = new StormFx { Obj = obj };
            }
        }

        // ==================== Fortress FX ====================

        void SpawnFortress(Vector3 origin, Vector3 target)
        {
            for (int i = 0; i < FortressFxPool; i++)
            {
                ref var f = ref _fortressFx[i];
                if (f.Active) continue;
                f.Active = true; f.Exploded = false;
                f.Origin = origin; f.Target = target;
                f.FramesTotal = FortressTravelFrames; f.FramesLeft = FortressTravelFrames;
                f.Ball.transform.position   = new Vector3(origin.x, 1.5f, origin.z);
                f.Ball.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);
                f.Ball.SetActive(true);
                f.Explosion.SetActive(false);
                return;
            }
        }

        void TickFortressFx()
        {
            for (int i = 0; i < FortressFxPool; i++)
            {
                ref var f = ref _fortressFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (!f.Exploded)
                {
                    float t = 1f - (float)f.FramesLeft / f.FramesTotal;
                    float px = Mathf.Lerp(f.Origin.x, f.Target.x, t);
                    float pz = Mathf.Lerp(f.Origin.z, f.Target.z, t);
                    float arcY = Mathf.Lerp(1.5f, 0.5f, t) + Mathf.Sin(t * Mathf.PI) * 2.5f;
                    f.Ball.transform.position = new Vector3(px, arcY, pz);
                    if (f.FramesLeft <= 0)
                    {
                        f.Exploded = true;
                        f.FramesTotal = FortressExplosionFrames; f.FramesLeft = FortressExplosionFrames;
                        f.Ball.SetActive(false);
                        f.Explosion.transform.position   = new Vector3(f.Target.x, 0.5f, f.Target.z);
                        f.Explosion.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                        f.Explosion.SetActive(true);
                    }
                }
                else
                {
                    float t = 1f - (float)f.FramesLeft / f.FramesTotal;
                    float s = t < 0.35f
                        ? Mathf.Lerp(0.5f, 7.0f, t / 0.35f)
                        : Mathf.Lerp(7.0f, 0.05f, (t - 0.35f) / 0.65f);
                    f.Explosion.transform.localScale = new Vector3(s, s * 0.4f, s);
                    if (f.FramesLeft <= 0) { f.Active = false; f.Explosion.SetActive(false); }
                }
            }
        }

        // ==================== Storm FX (electric arc) ====================

        void SpawnStorm(Vector3 origin, Vector3 target)
        {
            for (int i = 0; i < StormFxPool; i++)
            {
                ref var f = ref _stormFx[i];
                if (f.Active) continue;
                f.Active = true; f.Origin = origin; f.Target = target; f.FramesLeft = 4;
                f.Obj.SetActive(true);
                return;
            }
        }

        void TickStormFx()
        {
            for (int i = 0; i < StormFxPool; i++)
            {
                ref var f = ref _stormFx[i];
                if (!f.Active) continue;
                f.FramesLeft--;
                if (f.FramesLeft <= 0) { f.Active = false; f.Obj.SetActive(false); continue; }
                Vector3 mid = (f.Origin + f.Target) * 0.5f;
                Vector3 dir = f.Target - f.Origin;
                float len = dir.magnitude;
                float flicker = 0.06f + (f.FramesLeft % 2) * 0.04f; // electric flicker
                f.Obj.transform.position   = mid;
                f.Obj.transform.localScale = new Vector3(flicker, flicker, len);
                if (len > 0.01f) f.Obj.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
        }

        void CreateCellHighlight()
        {
            _cellHighlight = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _cellHighlight.name = "TD_CellHighlight";
            _cellHighlight.transform.SetParent(transform);
            _cellHighlight.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _cellHighlight.transform.localScale = new Vector3(0.95f, 0.95f, 1f);
            _matCellHighlight = new Material(
                Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard"))
            {
                color = new Color(1f, 0.92f, 0.2f, 0.55f)
            };
            // Enable transparency
            _matCellHighlight.SetFloat("_Surface", 1f);
            _matCellHighlight.renderQueue = 3000;
            _cellHighlightRend = _cellHighlight.GetComponent<Renderer>();
            _cellHighlightRend.material = _matCellHighlight;
            DestroyCollider(_cellHighlight);
            _cellHighlight.SetActive(false);
        }

        static void DestroyCollider(GameObject obj)
        {
            var col = obj.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        // ==================== World-space IMGUI bars ====================

        void OnGUI()
        {
            if (!_initialized || _state == null || _cam == null) return;

            // Lazy-create 1×1 white texture used for all bars
            if (_barTex == null)
            {
                _barTex = new Texture2D(1, 1);
                _barTex.SetPixels(new[] { Color.white });
                _barTex.Apply();
            }

            // ── Enemy HP bars ─────────────────────────────────────────────
            for (int i = 0; i < GameState.MaxEnemies; i++)
            {
                ref var e = ref _state.Enemies[i];
                if (!e.IsAlive) continue;

                Vector3 sp = _cam.WorldToScreenPoint(
                    new Vector3(e.PosX.ToFloat(), 0.75f, e.PosZ.ToFloat()));
                if (sp.z < 0f) continue;

                float cx = sp.x;
                float cy = Screen.height - sp.y - 6f; // slightly above enemy center
                int   maxHp = GameState.GetEnemyHp(e.Type);
                float ratio = maxHp > 0 ? Mathf.Clamp01((float)e.Hp / maxHp) : 0f;

                // Color: green (full) → yellow → red (low)
                Color hpCol = ratio > 0.5f
                    ? Color.Lerp(new Color(0.95f, 0.80f, 0.05f), new Color(0.15f, 0.90f, 0.10f), (ratio - 0.5f) * 2f)
                    : Color.Lerp(new Color(0.90f, 0.10f, 0.05f), new Color(0.95f, 0.80f, 0.05f), ratio * 2f);

                DrawBar(cx - 13f, cy, 26f, 4f, ratio, hpCol);
            }

            // ── Tower CD bars ─────────────────────────────────────────────
            for (int i = 0; i < GameState.GridSize; i++)
            {
                ref var t = ref _state.Grid[i];
                if (t.Type == TowerType.None) continue;

                int cx2 = i % GameState.GridW;
                int cy2 = i / GameState.GridW;
                Vector3 sp = _cam.WorldToScreenPoint(
                    new Vector3(cx2 + 0.5f, 2.1f, cy2 + 0.5f));
                if (sp.z < 0f) continue;

                float sx = sp.x;
                float sy = Screen.height - sp.y - 6f;
                int   maxCd = GameState.GetTowerCooldown(t.Type, t.Level);
                // fill: 0 = just fired (empty), 1 = fully cooled down (ready)
                float fill = maxCd > 0 ? 1f - Mathf.Clamp01((float)t.CooldownFrames / maxCd) : 1f;

                // Color: dim yellow while cooling → bright green when ready
                Color cdCol = fill >= 1f
                    ? new Color(0.25f, 1.00f, 0.25f)
                    : new Color(1.0f, Mathf.Lerp(0.45f, 0.85f, fill), 0.05f);

                DrawBar(sx - 16f, sy, 32f, 4f, fill, cdCol);
            }

            GUI.color = Color.white; // restore
        }

        void DrawBar(float x, float y, float w, float h, float fill, Color fillColor)
        {
            // Dark background
            GUI.color = new Color(0.06f, 0.06f, 0.06f, 0.82f);
            GUI.DrawTexture(new Rect(x, y, w, h), _barTex);
            // Colored fill
            if (fill > 0.005f)
            {
                GUI.color = fillColor;
                GUI.DrawTexture(new Rect(x, y, w * fill, h), _barTex);
            }
        }

        void OnDestroy()
        {
            Destroy(_matGround); Destroy(_matGridEven); Destroy(_matGridOdd);
            Destroy(_matBase); Destroy(_matBaseBeacon);
            Destroy(_matBasic); Destroy(_matFast); Destroy(_matTank);
            Destroy(_matArmored); Destroy(_matElite); Destroy(_matSlowed);
            Destroy(_matFxArrow); Destroy(_matFxBall); Destroy(_matFxExplosion);
            Destroy(_matFxMagic); Destroy(_matFxIce); Destroy(_matFxSniper);
            Destroy(_matFxFortressBall); Destroy(_matFxFortressExplosion); Destroy(_matFxStorm);
            Destroy(_matCellHighlight);
            if (_barTex != null) Destroy(_barTex);
            for (int i = 0; i < _matOwner.Length; i++) Destroy(_matOwner[i]);
            for (int ti = 1; ti <= 7; ti++)
                for (int lv = 1; lv <= 3; lv++)
                {
                    Destroy(_matTowerBase[ti, lv]);
                    Destroy(_matTowerDeco[ti, lv]);
                }
        }
    }
}
