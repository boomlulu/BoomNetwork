// BoomNetwork MinecraftDemo — Player Position Sync (IEntitySync)

using System;
using BoomNetwork.Core.FrameSync;
using UnityEngine;

namespace BoomNetwork.Samples.MinecraftDemo
{
    /// <summary>
    /// IEntitySync implementation for player position + rotation.
    /// Authority player: serializes transform to 16 bytes.
    /// Remote player: interpolates toward received state.
    /// </summary>
    public class MinecraftPlayerSync : MonoBehaviour, IEntitySync
    {
        [SerializeField] float interpolationSpeed = 12f;
        [SerializeField] float snapThreshold = 10f;

        int _entityId;
        bool _isAuthority;

        // Remote state for interpolation
        Vector3 _targetPosition;
        float _targetRotationY;
        bool _hasRemoteState;

        public int EntityId
        {
            get => _entityId;
            set => _entityId = value;
        }

        // 16 bytes: posX(4) + posY(4) + posZ(4) + rotY(4)
        public int StateSize => 16;

        public bool IsAuthority
        {
            get => _isAuthority;
            set => _isAuthority = value;
        }

        public void Init(int entityId, bool isAuthority)
        {
            _entityId = entityId;
            _isAuthority = isAuthority;
            _hasRemoteState = false;
        }

        /// <summary>Authority: write current position + rotation</summary>
        public int WriteState(byte[] buffer, int offset)
        {
            var pos = transform.position;
            BitConverter.TryWriteBytes(new Span<byte>(buffer, offset, 4), pos.x);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, offset + 4, 4), pos.y);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, offset + 8, 4), pos.z);
            BitConverter.TryWriteBytes(new Span<byte>(buffer, offset + 12, 4), transform.eulerAngles.y);
            return 16;
        }

        /// <summary>Remote: receive authority state and store for interpolation</summary>
        public void OnRemoteState(byte[] data, int offset, int length, int senderPlayerId)
        {
            if (length < 16) return;

            _targetPosition = new Vector3(
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8));
            _targetRotationY = BitConverter.ToSingle(data, offset + 12);
            _hasRemoteState = true;
        }

        void Update()
        {
            if (_isAuthority || !_hasRemoteState) return;

            // Interpolate remote player toward target
            float dist = Vector3.Distance(transform.position, _targetPosition);
            if (dist > snapThreshold)
            {
                // Snap for large corrections
                transform.position = _targetPosition;
            }
            else
            {
                transform.position = Vector3.Lerp(
                    transform.position, _targetPosition,
                    interpolationSpeed * Time.deltaTime);
            }

            float currentY = transform.eulerAngles.y;
            float newY = Mathf.LerpAngle(currentY, _targetRotationY, interpolationSpeed * Time.deltaTime);
            transform.eulerAngles = new Vector3(0, newY, 0);
        }
    }
}
