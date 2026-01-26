using System.Collections.Generic;
using UnityEngine;

namespace MyWinterCarMpMod.Util
{
    public sealed class DevFreecam
    {
        private static readonly string[] CameraDriverComponents = new string[]
        {
            "CarCamerasController",
            "CarCameras",
            "SmoothFollow",
            "S_Camera",
            "SmoothMouseLook",
            "SimpleSmoothMouseLook",
            "MouseLook",
            "UnityStandardAssets.Characters.FirstPerson.MouseLook"
        };

        private Camera _camera;
        private Transform _savedParent;
        private Vector3 _savedPosition;
        private Quaternion _savedRotation;
        private readonly List<Behaviour> _disabled = new List<Behaviour>();
        private bool _active;
        private float _yaw;
        private float _pitch;
        private float _moveSpeed = 6f;

        public bool IsActive
        {
            get { return _active; }
        }

        public float MoveSpeed
        {
            get { return _moveSpeed; }
            set { _moveSpeed = Mathf.Max(0.1f, value); }
        }

        public bool Enable(float speed)
        {
            if (_active)
            {
                return true;
            }

            Camera cam = FindCamera();
            if (cam == null)
            {
                DebugLog.Warn("DevFreecam: no camera found.");
                return false;
            }

            _camera = cam;
            _savedParent = cam.transform.parent;
            _savedPosition = cam.transform.position;
            _savedRotation = cam.transform.rotation;

            Vector3 euler = cam.transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);
            MoveSpeed = speed;

            DisableDrivers(cam.gameObject);
            cam.transform.parent = null;

            _active = true;
            DebugLog.Info("DevFreecam enabled.");
            return true;
        }

        public void Disable()
        {
            if (!_active)
            {
                return;
            }

            RestoreDrivers();

            if (_camera != null)
            {
                _camera.transform.SetParent(_savedParent);
                _camera.transform.position = _savedPosition;
                _camera.transform.rotation = _savedRotation;
            }

            _active = false;
            DebugLog.Info("DevFreecam disabled.");
        }

        public void Update()
        {
            if (!_active || _camera == null)
            {
                return;
            }

            UpdateRotation();
            UpdateMovement();
        }

        private void UpdateRotation()
        {
            if (!Input.GetMouseButton(1))
            {
                return;
            }

            float mouseX = Input.GetAxisRaw("Mouse X");
            float mouseY = Input.GetAxisRaw("Mouse Y");
            _yaw += mouseX * 3f;
            _pitch = Mathf.Clamp(_pitch - mouseY * 3f, -89f, 89f);
            _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void UpdateMovement()
        {
            float speed = _moveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                speed *= 3f;
            }
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                speed *= 0.25f;
            }

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W))
            {
                move += _camera.transform.forward;
            }
            if (Input.GetKey(KeyCode.S))
            {
                move -= _camera.transform.forward;
            }
            if (Input.GetKey(KeyCode.D))
            {
                move += _camera.transform.right;
            }
            if (Input.GetKey(KeyCode.A))
            {
                move -= _camera.transform.right;
            }
            if (Input.GetKey(KeyCode.E))
            {
                move += _camera.transform.up;
            }
            if (Input.GetKey(KeyCode.Q))
            {
                move -= _camera.transform.up;
            }

            if (move.sqrMagnitude > 0f)
            {
                _camera.transform.position += move.normalized * speed * Time.unscaledDeltaTime;
            }
        }

        private void DisableDrivers(GameObject cameraObj)
        {
            _disabled.Clear();
            DisableDriverChain(cameraObj);
            Transform parent = cameraObj.transform.parent;
            while (parent != null)
            {
                DisableDriverChain(parent.gameObject);
                parent = parent.parent;
            }
        }

        private void DisableDriverChain(GameObject obj)
        {
            if (obj == null)
            {
                return;
            }

            for (int i = 0; i < CameraDriverComponents.Length; i++)
            {
                Component comp = obj.GetComponent(CameraDriverComponents[i]);
                Behaviour behaviour = comp as Behaviour;
                if (behaviour != null && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    _disabled.Add(behaviour);
                }
            }
        }

        private void RestoreDrivers()
        {
            for (int i = 0; i < _disabled.Count; i++)
            {
                Behaviour behaviour = _disabled[i];
                if (behaviour != null)
                {
                    behaviour.enabled = true;
                }
            }
            _disabled.Clear();
        }

        private static Camera FindCamera()
        {
            Camera cam = Camera.main;
            if (IsCameraUsable(cam))
            {
                return cam;
            }

            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                if (IsCameraUsable(cams[i]))
                {
                    return cams[i];
                }
            }

            return null;
        }

        private static bool IsCameraUsable(Camera cam)
        {
            return cam != null && cam.enabled && cam.gameObject.activeInHierarchy;
        }

        private static float NormalizePitch(float pitch)
        {
            if (pitch > 180f)
            {
                pitch -= 360f;
            }
            return pitch;
        }
    }
}
