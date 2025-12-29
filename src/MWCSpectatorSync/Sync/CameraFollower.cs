using System;
using MWCSpectatorSync.Net;
using UnityEngine;

namespace MWCSpectatorSync.Sync
{
    public sealed class CameraFollower
    {
        private static readonly string[] PlayerCameraComponentNames = new string[]
        {
            "MainCamera",
            "CarCameras",
            "CarCamerasController",
            "SmoothFollow",
            "S_Camera"
        };

        private Camera _cachedCamera;

        public void Clear()
        {
            _cachedCamera = null;
        }

        public void Update(CameraStateData state, bool hasState, bool allowApply, float positionSmoothing, float rotationSmoothing)
        {
            if (!allowApply || !hasState)
            {
                return;
            }

            Camera cam = GetBestCamera();
            if (cam == null)
            {
                return;
            }

            Vector3 targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
            Quaternion targetRot = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);

            Transform t = cam.transform;
            float posLerp = Clamp01(positionSmoothing);
            float rotLerp = Clamp01(rotationSmoothing);

            Vector3 newPos = Vector3.Lerp(t.position, targetPos, posLerp);
            Quaternion newRot = Quaternion.Slerp(t.rotation, targetRot, rotLerp);
            t.SetPositionAndRotation(newPos, newRot);

            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, state.Fov, rotLerp);
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

        private static bool HasAnyComponent(Camera cam, string[] typeNames)
        {
            for (int i = 0; i < typeNames.Length; i++)
            {
                if (cam.GetComponent(typeNames[i]) != null)
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
