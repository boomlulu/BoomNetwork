// BoomNetwork VampireSurvivors Demo — Master 3D Renderer (Phase 2)
//
// Fixed orthographic top-down camera. Programmatic primitives.
// Supports: 3 enemy types, 4 weapon visuals, lightning flashes, orbs.

using System;
using UnityEngine;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public class VSRenderer : MonoBehaviour
    {
        GameState _state;
        int _localSlot;
        bool _initialized;

        Camera _cam;

        // Materials
        Material _matPlayer, _matPlayerHit;
        Material _matZombie, _matBat, _matMage;
        Material _matKnife, _matBoneShard;
        Material _matGem, _matGround;
        Material _matOrb, _matLightning, _matHolyWater;

        // Pools
        GameObject[] _playerObjs = new GameObject[GameState.MaxPlayers];
        Material[] _playerMats = new Material[GameState.MaxPlayers];
        GameObject[] _enemyPool = new GameObject[GameState.MaxEnemies];
        Renderer[] _enemyRenderers = new Renderer[GameState.MaxEnemies];
        GameObject[] _projPool = new GameObject[GameState.MaxProjectiles];
        Renderer[] _projRenderers = new Renderer[GameState.MaxProjectiles];
        GameObject[] _gemPool = new GameObject[GameState.MaxGems];

        // Orb pool: 4 players × 5 orbs = 20
        const int TotalOrbs = GameState.MaxPlayers * PlayerState.MaxOrbs;
        GameObject[] _orbPool = new GameObject[TotalOrbs];

        // Lightning flash pool
        GameObject[] _flashPool = new GameObject[GameState.MaxLightningFlashes];

        static readonly Color[] PlayerColors =
        {
            new Color(0.2f, 0.6f, 1f),
            new Color(0.2f, 1f, 0.4f),
            new Color(1f, 0.9f, 0.2f),
            new Color(1f, 0.4f, 0.9f),
        };

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
        }

        void CreateMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");

            _matPlayer = new Material(shader) { color = PlayerColors[0] };
            _matPlayerHit = new Material(shader) { color = new Color(1f, 0.5f, 0.5f, 0.7f) };
            _matZombie = new Material(shader) { color = new Color(0.9f, 0.2f, 0.15f) };
            _matBat = new Material(shader) { color = new Color(0.5f, 0.1f, 0.6f) };
            _matMage = new Material(shader) { color = new Color(0.2f, 0.2f, 0.8f) };
            _matKnife = new Material(shader) { color = Color.white };
            _matBoneShard = new Material(shader) { color = new Color(0.9f, 0.85f, 0.7f) };
            _matGem = new Material(shader) { color = new Color(0.3f, 1f, 0.5f) };
            _matGround = new Material(shader) { color = new Color(0.15f, 0.15f, 0.2f) };
            _matOrb = new Material(shader) { color = new Color(0.4f, 0.7f, 1f) };
            _matOrb.SetFloat("_Smoothness", 0.9f);
            _matLightning = new Material(shader) { color = new Color(1f, 1f, 0.5f) };
            _matLightning.EnableKeyword("_EMISSION");
            _matHolyWater = new Material(shader) { color = new Color(0.3f, 0.5f, 1f, 0.5f) };
        }

        void CreateCamera()
        {
            var camObj = new GameObject("VS_Camera");
            camObj.transform.SetParent(transform);
            _cam = camObj.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
            _cam.orthographic = true;
            _cam.orthographicSize = GameState.ArenaHalfSize.ToFloat() + 2f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 60f;
            camObj.transform.position = new Vector3(0f, 40f, 0f);
            camObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var mainCam = Camera.main;
            if (mainCam != null && mainCam != _cam)
                mainCam.gameObject.SetActive(false);
        }

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

            // Arena border
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
                obj.GetComponent<Renderer>().sharedMaterial = _matOrb;
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

        // ==================== SyncVisuals ====================

        public void SyncVisuals()
        {
            if (!_initialized || _state == null) return;

            SyncPlayers();
            SyncEnemies();
            SyncProjectiles();
            SyncGems();
            SyncOrbs();
            SyncFlashes();
        }

        void SyncPlayers()
        {
            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref _state.Players[i];
                bool show = p.IsActive && p.IsAlive;
                _playerObjs[i].SetActive(show);
                if (!show) continue;

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
                _enemyPool[i].SetActive(show);
                if (!show) continue;

                float ex = e.PosX.ToFloat(), ez = e.PosZ.ToFloat();
                _enemyPool[i].transform.position = new Vector3(ex, 0.45f, ez);

                switch (e.Type)
                {
                    case EnemyType.Zombie:
                        _enemyPool[i].transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                        _enemyRenderers[i].sharedMaterial = _matZombie;
                        break;
                    case EnemyType.Bat:
                        _enemyPool[i].transform.localScale = new Vector3(0.5f, 0.4f, 0.5f);
                        _enemyPool[i].transform.position = new Vector3(ex, 0.8f, ez);
                        _enemyRenderers[i].sharedMaterial = _matBat;
                        break;
                    case EnemyType.SkeletonMage:
                        _enemyPool[i].transform.localScale = new Vector3(0.6f, 1.1f, 0.6f);
                        _enemyRenderers[i].sharedMaterial = _matMage;
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
                        _projPool[i].transform.localScale = new Vector3(0.1f, 0.1f, 0.35f);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.5f, p.PosZ.ToFloat());
                        _projRenderers[i].sharedMaterial = _matKnife;
                        if (p.DirX != FInt.Zero || p.DirZ != FInt.Zero)
                        {
                            float angle = Mathf.Atan2(p.DirX.ToFloat(), p.DirZ.ToFloat()) * Mathf.Rad2Deg;
                            _projPool[i].transform.rotation = Quaternion.Euler(0f, angle, 0f);
                        }
                        break;

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
                        float diameter = p.Radius.ToFloat() * 2f;
                        _projPool[i].transform.localScale = new Vector3(diameter, 0.05f, diameter);
                        _projPool[i].transform.position = new Vector3(p.PosX.ToFloat(), 0.02f, p.PosZ.ToFloat());
                        _projPool[i].transform.rotation = Quaternion.identity;
                        _projRenderers[i].sharedMaterial = _matHolyWater;
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
                if (show)
                {
                    float bob = Mathf.Sin((_state.FrameNumber + i * 7) * 0.15f) * 0.1f;
                    _gemPool[i].transform.position = new Vector3(g.PosX.ToFloat(), 0.2f + bob, g.PosZ.ToFloat());
                }
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
                }
            }
        }

        void SyncFlashes()
        {
            for (int i = 0; i < GameState.MaxLightningFlashes; i++)
            {
                ref var f = ref _state.Flashes[i];
                bool show = f.FramesLeft > 0;
                _flashPool[i].SetActive(show);
                if (show)
                    _flashPool[i].transform.position = new Vector3(f.PosX.ToFloat(), 1f, f.PosZ.ToFloat());
            }
        }

        // ==================== Cleanup ====================

        static void DestroyCollider(GameObject obj)
        {
            var col = obj.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        void OnDestroy()
        {
            Destroy(_matPlayer); Destroy(_matPlayerHit);
            Destroy(_matZombie); Destroy(_matBat); Destroy(_matMage);
            Destroy(_matKnife); Destroy(_matBoneShard);
            Destroy(_matGem); Destroy(_matGround);
            Destroy(_matOrb); Destroy(_matLightning); Destroy(_matHolyWater);
            for (int i = 0; i < _playerMats.Length; i++)
                if (_playerMats[i] != null) Destroy(_playerMats[i]);
        }
    }
}
