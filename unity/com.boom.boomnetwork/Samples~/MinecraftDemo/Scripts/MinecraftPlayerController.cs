// BoomNetwork MinecraftDemo — First-Person Player Controller

using Unity.Mathematics;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// FPS camera controller with block interaction (raycast + place/break).
    /// Handles local player input; networked actions go through MinecraftNetworkManager.
    /// </summary>
    public class MinecraftPlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] float moveSpeed = 6f;
        [SerializeField] float jumpForce = 7f;
        [SerializeField] float gravity = -18f;
        [SerializeField] float mouseSensitivity = 2f;

        [Header("Block Interaction")]
        [SerializeField] float reachDistance = 6f;
        [SerializeField] LayerMask blockLayer = ~0;

        CharacterController _cc;
        Transform _cameraTransform;
        float _verticalVelocity;
        float _cameraPitch;

        // Block interaction state
        public bool HasTarget { get; private set; }
        public int3 TargetBlockPos { get; private set; }
        public int3 PlaceBlockPos { get; private set; }
        public Vector3 TargetHitPoint { get; private set; }

        // Pending block action (consumed by network manager each frame)
        public BlockAction PendingAction { get; set; }

        public struct BlockAction
        {
            public byte ActionType; // 0=none, 1=break, 2=place
            public int3 Position;
            public BlockType BlockType;
        }

        // Selected block type for placement
        public BlockType SelectedBlockType { get; set; } = BlockType.Stone;

        void Start()
        {
            _cc = GetComponent<CharacterController>();
            if (_cc == null)
                _cc = gameObject.AddComponent<CharacterController>();
            _cc.height = 1.8f;
            _cc.radius = 0.3f;
            _cc.center = new Vector3(0, 0.9f, 0);

            // Create camera as child
            _cameraTransform = GetComponentInChildren<Camera>()?.transform;
            if (_cameraTransform == null)
            {
                var camGo = new GameObject("PlayerCamera");
                camGo.transform.SetParent(transform, false);
                camGo.transform.localPosition = new Vector3(0, 1.6f, 0);
                var cam = camGo.AddComponent<Camera>();
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.fieldOfView = 70f;
                _cameraTransform = cam.transform;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void Update()
        {
            HandleMouseLook();
            HandleMovement();
            HandleBlockRaycast();
            HandleBlockInput();
            HandleBlockSelection();
        }

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

        void HandleMovement()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            Vector3 move = transform.right * h + transform.forward * v;
            if (move.sqrMagnitude > 1f) move.Normalize();
            move *= moveSpeed;

            if (_cc.isGrounded)
            {
                _verticalVelocity = -1f; // small downward force to stay grounded
                if (Input.GetButtonDown("Jump"))
                    _verticalVelocity = jumpForce;
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }

            move.y = _verticalVelocity;
            _cc.Move(move * Time.deltaTime);
        }

        void HandleBlockRaycast()
        {
            Ray ray = new Ray(_cameraTransform.position, _cameraTransform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, reachDistance, blockLayer))
            {
                HasTarget = true;
                TargetHitPoint = hit.point;

                // The hit point is on the surface of a block.
                // To find which block: step slightly into the face (for break) or back (for place)
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

            // Left click = break
            if (Input.GetMouseButtonDown(0))
            {
                PendingAction = new BlockAction
                {
                    ActionType = 1,
                    Position = TargetBlockPos,
                    BlockType = BlockType.Air,
                };
            }
            // Right click = place
            else if (Input.GetMouseButtonDown(1))
            {
                PendingAction = new BlockAction
                {
                    ActionType = 2,
                    Position = PlaceBlockPos,
                    BlockType = SelectedBlockType,
                };
            }
        }

        void HandleBlockSelection()
        {
            // Number keys 1-7 to select block type
            for (int i = 1; i <= 7; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i))
                {
                    SelectedBlockType = (BlockType)i;
                }
            }

            // Scroll wheel
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

            // Toggle cursor lock with Escape
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
