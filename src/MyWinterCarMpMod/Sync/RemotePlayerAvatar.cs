using MyWinterCarMpMod.Net;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class RemotePlayerAvatar
    {
        private GameObject _avatar;
        private bool _initialized;
        private long _lastStateTimeMs;
        private Renderer[] _renderers;

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

            _avatar = new GameObject("MWC Remote Player");
            Object.DontDestroyOnLoad(_avatar);

            GameObject body = CreatePrimitiveChild(_avatar.transform, "Body", PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.9f, 0.45f));
            GameObject head = CreatePrimitiveChild(_avatar.transform, "Head", PrimitiveType.Sphere, new Vector3(0f, 1.55f, 0f), new Vector3(0.3f, 0.3f, 0.3f));
            CreatePrimitiveChild(_avatar.transform, "Hip", PrimitiveType.Cube, new Vector3(0f, 0.55f, 0f), new Vector3(0.35f, 0.2f, 0.25f));
            CreatePrimitiveChild(_avatar.transform, "ArmL", PrimitiveType.Cube, new Vector3(-0.3f, 1.1f, 0f), new Vector3(0.15f, 0.45f, 0.15f));
            CreatePrimitiveChild(_avatar.transform, "ArmR", PrimitiveType.Cube, new Vector3(0.3f, 1.1f, 0f), new Vector3(0.15f, 0.45f, 0.15f));

            _renderers = _avatar.GetComponentsInChildren<Renderer>(true);
            SetColor(new Color(0.2f, 0.8f, 1f, 0.9f));
        }

        private static GameObject CreatePrimitiveChild(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 localScale)
        {
            GameObject obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPos;
            obj.transform.localScale = localScale;

            Collider collider = obj.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            return obj;
        }

        private void SetColor(Color color)
        {
            if (_renderers == null)
            {
                return;
            }

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null)
                {
                    continue;
                }
                renderer.material.color = color;
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
