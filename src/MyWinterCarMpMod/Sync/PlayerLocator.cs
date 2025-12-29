using System;
using MyWinterCarMpMod.Net;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class PlayerLocator
    {
        private static readonly string[] PlayerCameraComponentNames = new string[]
        {
            "MainCamera",
            "CarCameras",
            "CarCamerasController",
            "SmoothFollow",
            "S_Camera"
        };

        private static readonly string[] PlayerBodyComponentNames = new string[]
        {
            "CharacterMotor",
            "FPSInputController",
            "FirstPersonController",
            "RigidbodyFirstPersonController",
            "ThirdPersonController"
        };

        private Camera _cachedCamera;
        private Transform _cachedBody;
        private Transform _cachedView;

        public void Clear()
        {
            _cachedCamera = null;
            _cachedBody = null;
            _cachedView = null;
        }

        public bool TryGetLocalState(out PlayerStateData state)
        {
            state = new PlayerStateData();
            Transform body;
            Transform view;
            if (!TryResolvePlayer(out body, out view))
            {
                return false;
            }

            if (body == null)
            {
                body = view;
            }

            if (view == null)
            {
                view = body;
            }

            if (body == null || view == null)
            {
                return false;
            }

            Vector3 pos = body.position;
            Quaternion viewRot = view.rotation;
            state.UnixTimeMs = GetUnixTimeMs();
            state.PosX = pos.x;
            state.PosY = pos.y;
            state.PosZ = pos.z;
            state.ViewRotX = viewRot.x;
            state.ViewRotY = viewRot.y;
            state.ViewRotZ = viewRot.z;
            state.ViewRotW = viewRot.w;
            return true;
        }

        private bool TryResolvePlayer(out Transform body, out Transform view)
        {
            body = null;
            view = null;

            if (IsViewValid(_cachedView) && IsBodyValid(_cachedBody))
            {
                body = _cachedBody;
                view = _cachedView;
                return true;
            }

            Camera cam = GetBestCamera();
            view = cam != null ? cam.transform : null;
            body = FindBodyFromView(view);
            if (body == null)
            {
                body = view;
            }

            if (body == null && view == null)
            {
                return false;
            }

            _cachedCamera = cam;
            _cachedBody = body;
            _cachedView = view;
            return true;
        }

        private Camera GetBestCamera()
        {
            if (IsCameraUsable(_cachedCamera))
            {
                return _cachedCamera;
            }

            Camera cam = Camera.main;
            if (IsCameraUsable(cam))
            {
                _cachedCamera = cam;
                return cam;
            }

            Camera best = null;
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (!IsCameraUsable(candidate))
                {
                    continue;
                }

                if (IsLikelyPlayerCamera(candidate))
                {
                    _cachedCamera = candidate;
                    return candidate;
                }

                if (best == null)
                {
                    best = candidate;
                }
            }

            _cachedCamera = best;
            return best;
        }

        private static Transform FindBodyFromView(Transform view)
        {
            Transform current = view;
            while (current != null)
            {
                if (current.GetComponent<CharacterController>() != null)
                {
                    return current;
                }
                if (HasAnyComponent(current.gameObject, PlayerBodyComponentNames))
                {
                    return current;
                }
                current = current.parent;
            }
            return null;
        }

        private static bool IsViewValid(Transform view)
        {
            return view != null && view.gameObject.activeInHierarchy;
        }

        private static bool IsBodyValid(Transform body)
        {
            return body != null && body.gameObject.activeInHierarchy;
        }

        private static bool IsCameraUsable(Camera cam)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                return false;
            }
            return !IsMapCamera(cam);
        }

        private static bool IsLikelyPlayerCamera(Camera cam)
        {
            if (cam == null)
            {
                return false;
            }

            if (cam.CompareTag("MainCamera") || string.Equals(cam.name, "MainCamera", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (cam.GetComponent<AudioListener>() != null)
            {
                return true;
            }

            return HasAnyComponent(cam, PlayerCameraComponentNames);
        }

        private static bool HasAnyComponent(GameObject obj, string[] typeNames)
        {
            if (obj == null)
            {
                return false;
            }

            for (int i = 0; i < typeNames.Length; i++)
            {
                if (obj.GetComponent(typeNames[i]) != null)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsMapCamera(Camera cam)
        {
            if (cam == null)
            {
                return false;
            }

            GameObject obj = cam.gameObject;
            if (IsMapCameraObject(obj))
            {
                return true;
            }

            Transform parent = obj != null ? obj.transform.parent : null;
            if (parent != null && IsMapCameraObject(parent.gameObject))
            {
                return true;
            }

            return false;
        }

        private static bool IsMapCameraObject(GameObject obj)
        {
            if (obj == null)
            {
                return false;
            }
            if (string.Equals(obj.name, "MapCamera", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return obj.CompareTag("MapCamera");
        }

        private static long GetUnixTimeMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }
    }
}
