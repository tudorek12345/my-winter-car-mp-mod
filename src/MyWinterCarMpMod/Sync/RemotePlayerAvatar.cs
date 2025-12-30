using MyWinterCarMpMod.Net;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class RemotePlayerAvatar
    {
        private GameObject _avatar;
        private bool _initialized;
        private long _lastStateTimeMs;

        public void Update(PlayerStateData state, bool hasState, bool allowApply, float positionSmoothing, float rotationSmoothing)
        {
            if (!allowApply || !hasState)
            {
                return;
            }

            if (state.UnixTimeMs > 0 && state.UnixTimeMs <= _lastStateTimeMs)
            {
                return;
            }

            EnsureAvatar();

            Vector3 targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
            Quaternion viewRot = new Quaternion(state.ViewRotX, state.ViewRotY, state.ViewRotZ, state.ViewRotW);
            Quaternion targetRot = YawFrom(viewRot);

            Transform t = _avatar.transform;
            float posLerp = Clamp01(positionSmoothing);
            float rotLerp = Clamp01(rotationSmoothing);

            if (!_initialized)
            {
                t.position = targetPos;
                t.rotation = targetRot;
                _initialized = true;
                _lastStateTimeMs = state.UnixTimeMs;
                return;
            }

            t.position = Vector3.Lerp(t.position, targetPos, posLerp);
            t.rotation = Quaternion.Slerp(t.rotation, targetRot, rotLerp);
            _lastStateTimeMs = state.UnixTimeMs;
        }

        public void Clear()
        {
            if (_avatar != null)
            {
                Object.Destroy(_avatar);
                _avatar = null;
            }
            _initialized = false;
            _lastStateTimeMs = 0;
        }

        private void EnsureAvatar()
        {
            if (_avatar != null)
            {
                return;
            }

            _avatar = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _avatar.name = "MWC Remote Player";
            Object.DontDestroyOnLoad(_avatar);

            Collider collider = _avatar.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            Renderer renderer = _avatar.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            }
        }

        private static Quaternion YawFrom(Quaternion viewRot)
        {
            Vector3 euler = viewRot.eulerAngles;
            euler.x = 0f;
            euler.z = 0f;
            return Quaternion.Euler(euler);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }
            if (value > 1f)
            {
                return 1f;
            }
            return value;
        }
    }
}
