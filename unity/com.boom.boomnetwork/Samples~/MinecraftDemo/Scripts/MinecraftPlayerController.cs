// BoomNetwork MinecraftDemo — First-Person Player Controller
//
// Uses voxel AABB collision instead of CharacterController + MeshCollider.
// Directly queries block data — zero gap, zero penetration, zero physics jitter.

using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    public class MinecraftPlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] float moveSpeed = 5.5f;
        [SerializeField] float jumpForce = 7.5f;
        [SerializeField] float gravity = -16f;
        [SerializeField] float mouseSensitivity = 2f;

        [Header("Player Size (blocks)")]
        [SerializeField] float playerWidth = 0.6f;   // X/Z diameter
        [SerializeField] float playerHeight = 1.7f;   // total height
        [SerializeField] float eyeHeight = 1.55f;

        [Header("Block Interaction")]
        [SerializeField] float reachDistance = 6f;
        [SerializeField] LayerMask blockLayer = ~0;

        Transform _cameraTransform;
        float _cameraPitch;

        // Physics state (voxel-based, no CharacterController)
        Vector3 _velocity;
        bool _grounded;

        // Block interaction state
        public bool HasTarget { get; private set; }
        public int3 TargetBlockPos { get; private set; }
        public int3 PlaceBlockPos { get; private set; }


        public BlockAction PendingAction { get; set; }

        public struct BlockAction
        {
            public byte ActionType; // 0=none, 1=break, 2=place
            public int3 Position;
            public BlockType BlockType;
        }

        public BlockType SelectedBlockType { get; set; } = BlockType.Stone;
        public VoxelWorld World { get; set; }

        float HalfWidth => playerWidth * 0.5f;

        void Start()
        {
            // Remove CharacterController if present (legacy)
            var cc = GetComponent<CharacterController>();
            if (cc != null) Destroy(cc);

            // Remove any collider (we don't use Unity physics for player)
            var col = GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Camera
            _cameraTransform = GetComponentInChildren<Camera>()?.transform;
            if (_cameraTransform == null)
            {
                var camGo = new GameObject("PlayerCamera");
                camGo.transform.SetParent(transform, false);
                camGo.transform.localPosition = new Vector3(0, eyeHeight, 0);
                var cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.fieldOfView = 70f;
                _cameraTransform = cam.transform;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            // H3 WebGL: 浏览器安全策略要求 Pointer Lock 必须在用户手势（点击）回调中触发。
            // 在 Start() 中直接调用会被浏览器静默忽略，导致鼠标无法锁定。
            // WebGL 下改为"首次点击时锁定"，见 Update() 中的 _waitingForClick 逻辑。
            _waitingForClick = true;
            Cursor.visible = true;
#else
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private bool _waitingForClick;
#endif

        void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // H3 WebGL: 等待用户点击以锁定鼠标（浏览器安全策略：必须在用户手势中调用）
            if (_waitingForClick && UnityEngine.Input.GetMouseButtonDown(0))
            {
                _waitingForClick = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
#endif
            HandleMouseLook();
            HandleVoxelMovement();
            HandleBlockRaycast();
            HandleBlockInput();
            HandleBlockSelection();
        }

        // ========================= Mouse Look =========================

        void HandleMouseLook()
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;

            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            _cameraPitch -= mouseY;
            _cameraPitch = Mathf.Clamp(_cameraPitch, -89f, 89f);

            _cameraTransform.localEulerAngles = new Vector3(_cameraPitch, 0, 0);
            transform.Rotate(Vector3.up * mouseX);
        }

        // ========================= Voxel Movement =========================

        void HandleVoxelMovement()
        {
            if (World == null) return;

            float dt = Time.deltaTime;

            // Input
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 wishDir = transform.right * h + transform.forward * v;
            wishDir.y = 0;
            if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

            // Horizontal velocity (instant, no acceleration for crispy feel)
            _velocity.x = wishDir.x * moveSpeed;
            _velocity.z = wishDir.z * moveSpeed;

            // Ground check FIRST, before jump input
            _grounded = CheckGrounded(transform.position);

            // Gravity + jump
            if (_grounded)
            {
                if (_velocity.y < 0) _velocity.y = 0; // landed, stop falling
                if (Input.GetButtonDown("Jump"))
                    _velocity.y = jumpForce;
            }
            else
            {
                _velocity.y += gravity * dt;
                _velocity.y = Mathf.Max(_velocity.y, -40f); // terminal velocity
            }

            // Move with voxel collision (resolve each axis independently)
            Vector3 pos = transform.position;
            Vector3 delta = _velocity * dt;

            // Resolve Y first (gravity/jump), then X, then Z
            pos = MoveAxis(pos, 1, delta.y);
            pos = MoveAxis(pos, 0, delta.x);
            pos = MoveAxis(pos, 2, delta.z);

            transform.position = pos;
        }

        /// <summary>
        /// Move along one axis, stop at first solid block collision.
        /// Player AABB: center at (pos.x, pos.y + height/2, pos.z), size (width, height, width).
        /// pos.y = bottom of player (feet).
        /// </summary>
        Vector3 MoveAxis(Vector3 pos, int axis, float delta)
        {
            if (Mathf.Abs(delta) < 0.0001f) return pos;

            Vector3 newPos = pos;
            switch (axis)
            {
                case 0: newPos.x += delta; break;
                case 1: newPos.y += delta; break;
                case 2: newPos.z += delta; break;
            }

            if (!CollidesWithWorld(newPos))
                return newPos;

            // Collision: snap to block edge
            if (axis == 1) // Y axis
            {
                if (delta < 0) // falling — snap feet to top of block below
                {
                    newPos.y = Mathf.Ceil(pos.y + delta) ;
                    _velocity.y = 0;
                }
                else // jumping up — snap head to bottom of block above
                {
                    newPos.y = Mathf.Floor(pos.y + playerHeight + delta) - playerHeight;
                    _velocity.y = 0;
                }
            }
            else // X or Z axis — snap to block edge
            {
                float hw = HalfWidth;
                float center = axis == 0 ? pos.x : pos.z;
                if (delta > 0)
                    SetAxisF(ref newPos, axis, Mathf.Ceil(center + hw + delta) - 1f - hw + 0.001f);
                else
                    SetAxisF(ref newPos, axis, Mathf.Floor(center - hw + delta) + hw + 0.001f);

                if (axis == 0) _velocity.x = 0;
                else _velocity.z = 0;
            }

            // Double check the snapped position doesn't still collide
            if (CollidesWithWorld(newPos))
                return pos; // give up, stay where we are

            return newPos;
        }

        /// <summary>
        /// Check if player AABB at given position overlaps any solid block.
        /// Scans all block cells that the AABB touches.
        /// </summary>
        bool CollidesWithWorld(Vector3 pos)
        {
            float hw = HalfWidth;

            // AABB in world space: [minX, minY, minZ] to [maxX, maxY, maxZ]
            int minBX = Mathf.FloorToInt(pos.x - hw + 0.001f);
            int maxBX = Mathf.FloorToInt(pos.x + hw - 0.001f);
            int minBY = Mathf.FloorToInt(pos.y + 0.001f);
            int maxBY = Mathf.FloorToInt(pos.y + playerHeight - 0.001f);
            int minBZ = Mathf.FloorToInt(pos.z - hw + 0.001f);
            int maxBZ = Mathf.FloorToInt(pos.z + hw - 0.001f);

            for (int bx = minBX; bx <= maxBX; bx++)
            for (int by = minBY; by <= maxBY; by++)
            for (int bz = minBZ; bz <= maxBZ; bz++)
            {
                if (World.GetBlock(new int3(bx, by, bz)).IsSolid())
                    return true;
            }
            return false;
        }

        bool CheckGrounded(Vector3 pos)
        {
            float hw = HalfWidth;
            float checkY = pos.y - 0.05f; // slightly below feet

            int minBX = Mathf.FloorToInt(pos.x - hw + 0.001f);
            int maxBX = Mathf.FloorToInt(pos.x + hw - 0.001f);
            int minBZ = Mathf.FloorToInt(pos.z - hw + 0.001f);
            int maxBZ = Mathf.FloorToInt(pos.z + hw - 0.001f);
            int by = Mathf.FloorToInt(checkY);

            for (int bx = minBX; bx <= maxBX; bx++)
            for (int bz = minBZ; bz <= maxBZ; bz++)
            {
                if (World.GetBlock(new int3(bx, by, bz)).IsSolid())
                    return true;
            }
            return false;
        }

        static void SetAxisF(ref Vector3 v, int axis, float val)
        {
            switch (axis)
            {
                case 0: v.x = val; break;
                case 1: v.y = val; break;
                case 2: v.z = val; break;
            }
        }

        // ========================= Block Interaction =========================

        void HandleBlockRaycast()
        {
            Ray ray = new Ray(_cameraTransform.position, _cameraTransform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, reachDistance, blockLayer))
            {
                HasTarget = true;
                Vector3 breakPoint = hit.point + hit.normal * -0.01f;
                TargetBlockPos = new int3(
                    Mathf.FloorToInt(breakPoint.x),
                    Mathf.FloorToInt(breakPoint.y),
                    Mathf.FloorToInt(breakPoint.z));

                Vector3 placePoint = hit.point + hit.normal * 0.01f;
                PlaceBlockPos = new int3(
                    Mathf.FloorToInt(placePoint.x),
                    Mathf.FloorToInt(placePoint.y),
                    Mathf.FloorToInt(placePoint.z));
            }
            else
            {
                HasTarget = false;
            }
        }

        void HandleBlockInput()
        {
            if (!HasTarget) return;

            if (Input.GetMouseButtonDown(0))
            {
                PendingAction = new BlockAction
                {
                    ActionType = 1,
                    Position = TargetBlockPos,
                    BlockType = BlockType.Air,
                };
                return;
            }

            if (!Input.GetMouseButtonDown(1)) return;
            if (OverlapsPlayerBody(PlaceBlockPos)) return;

            PendingAction = new BlockAction
            {
                ActionType = 2,
                Position = PlaceBlockPos,
                BlockType = SelectedBlockType,
            };
        }

        bool OverlapsPlayerBody(int3 blockPos)
        {
            float hw = HalfWidth;
            var pos = transform.position;

            bool overlapX = blockPos.x < pos.x + hw && blockPos.x + 1 > pos.x - hw;
            bool overlapY = blockPos.y < pos.y + playerHeight && blockPos.y + 1 > pos.y;
            bool overlapZ = blockPos.z < pos.z + hw && blockPos.z + 1 > pos.z - hw;

            return overlapX && overlapY && overlapZ;
        }

        // ========================= Block Selection =========================

        void HandleBlockSelection()
        {
            for (int i = 1; i <= 7; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i))
                    SelectedBlockType = (BlockType)i;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll > 0f)
            {
                int next = (int)SelectedBlockType + 1;
                if (next >= BlockTypeExt.Count) next = 1;
                SelectedBlockType = (BlockType)next;
            }
            else if (scroll < 0f)
            {
                int prev = (int)SelectedBlockType - 1;
                if (prev < 1) prev = BlockTypeExt.Count - 1;
                SelectedBlockType = (BlockType)prev;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Cursor.lockState == CursorLockMode.Locked)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
        }
    }
}
