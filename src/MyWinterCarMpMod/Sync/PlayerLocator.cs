using System;
using System.Reflection;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
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
            "S_Camera",
            "SmoothMouseLook",
            "SimpleSmoothMouseLook",
            "MouseLook",
            "UnityStandardAssets.Characters.FirstPerson.MouseLook"
        };

        private static readonly string[] PlayerBodyComponentNames = new string[]
        {
            "CharacterMotor",
            "FPSInputController",
            "FirstPersonController",
            "RigidbodyFirstPersonController",
            "ThirdPersonController",
            "UnityStandardAssets.Characters.FirstPerson.RigidbodyFirstPersonController",
            "UnityStandardAssets.Characters.FirstPerson.FirstPersonController"
        };

        private static readonly string[] StandardControllerTypeNames = new string[]
        {
            "UnityStandardAssets.Characters.FirstPerson.RigidbodyFirstPersonController",
            "UnityStandardAssets.Characters.FirstPerson.FirstPersonController"
        };

        private Camera _cachedCamera;
        private Transform _cachedBody;
        private Transform _cachedView;
        private float _nextFailureLogTime;
        private float _nextResolveLogTime;
        private int _cachedLevelIndex = int.MinValue;
        private string _cachedLevelName = string.Empty;
        private bool _readyThisScene;
        private bool _loggedReady;

        public bool IsReady
        {
            get { return _readyThisScene; }
        }

        public void Clear()
        {
            _cachedCamera = null;
            _cachedBody = null;
            _cachedView = null;
            _readyThisScene = false;
            _loggedReady = false;
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

            _readyThisScene = true;
            if (!_loggedReady)
            {
                DebugLog.Info("PlayerLocator: ready in scene " + (_cachedLevelName ?? Application.loadedLevelName ?? "<unknown>"));
                _loggedReady = true;
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

        public bool TryGetLocalTransforms(out Transform body, out Transform view)
        {
            body = null;
            view = null;

            Transform resolvedBody;
            Transform resolvedView;
            if (!TryResolvePlayer(out resolvedBody, out resolvedView))
            {
                return false;
            }

            if (resolvedBody == null)
            {
                resolvedBody = resolvedView;
            }

            if (resolvedView == null)
            {
                resolvedView = resolvedBody;
            }

            if (resolvedBody == null && resolvedView == null)
            {
                return false;
            }

            body = resolvedBody;
            view = resolvedView;
            return true;
        }

        private bool TryResolvePlayer(out Transform body, out Transform view)
        {
            body = null;
            view = null;

            CheckForLevelChange();
            if (IsMainMenuSceneName(Application.loadedLevelName))
            {
                return false;
            }

            if (IsViewValid(_cachedView) && IsBodyValid(_cachedBody))
            {
                Camera cachedCam = _cachedView != null ? _cachedView.GetComponent<Camera>() : null;
                if ((cachedCam == null || IsCameraUsable(cachedCam)) && IsLikelyPlayerTransform(_cachedBody, _cachedView))
                {
                    body = _cachedBody;
                    view = _cachedView;
                    return true;
                }
            }

            if (TryResolveStandardController(out body, out view))
            {
                Cache(body, view);
                LogResolve(body, view, "standard-controller");
                return true;
            }

            Transform playerRoot = FindPlayerRoot();
            if (playerRoot != null)
            {
                Transform playerView = FindCameraUnder(playerRoot);
                if (!IsLikelyPlayerTransform(playerRoot, playerView))
                {
                    playerRoot = null;
                    playerView = null;
                }

                body = playerRoot;
                view = playerView;

                if (view != null && !IsInActiveScene(view))
                {
                    view = null;
                }
            }

            if (view == null)
            {
                Camera cam = GetBestCamera();
                view = cam != null ? cam.transform : null;
                if (view != null && !IsInActiveScene(view))
                {
                    view = null;
                }
            }

            if (body == null)
            {
                body = FindBodyFromView(view);
            }

            if (body == null)
            {
                body = FindBodyFromControllers();
            }

            if (body == null)
            {
                body = view;
            }

            if (body == null && view == null)
            {
                LogFailure();
                return false;
            }

            Cache(body, view);
            LogResolve(body, view, "fallback");
            return true;
        }

        private static Transform FindPlayerRoot()
        {
            try
            {
                GameObject tagged = GameObject.FindGameObjectWithTag("Player");
                if (tagged != null)
                {
                    if (!IsInActiveScene(tagged.transform))
                    {
                        return null;
                    }
                    return tagged.transform;
                }
            }
            catch (UnityException)
            {
            }

            string[] names = new string[]
            {
                "Player",
                "PLAYER",
                "player",
                "FPSController",
                "FirstPersonController"
            };

            for (int i = 0; i < names.Length; i++)
            {
                GameObject obj = GameObject.Find(names[i]);
                if (obj != null)
                {
                    if (!IsInActiveScene(obj.transform))
                    {
                        continue;
                    }
                    return obj.transform;
                }
            }

            return null;
        }

        private static Transform FindCameraUnder(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            Camera cam = root.GetComponentInChildren<Camera>();
            if (IsCameraUsable(cam))
            {
                return cam.transform;
            }

            return null;
        }

        private static Transform FindBodyFromControllers()
        {
            CharacterController[] controllers = UnityEngine.Object.FindObjectsOfType<CharacterController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                CharacterController controller = controllers[i];
                if (controller != null && controller.enabled && controller.gameObject.activeInHierarchy)
                {
                    if (!IsInActiveScene(controller.transform))
                    {
                        continue;
                    }
                    return controller.transform;
                }
            }

            Rigidbody[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null || !body.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (!IsInActiveScene(body.transform))
                {
                    continue;
                }
                if (NameContains(body.name, "player") || NameContains(body.gameObject.name, "player"))
                {
                    return body.transform;
                }
            }

            return null;
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
                if (!IsInActiveScene(current))
                {
                    return null;
                }
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
            return view != null && view.gameObject.activeInHierarchy && IsInActiveScene(view);
        }

        private static bool IsBodyValid(Transform body)
        {
            return body != null && body.gameObject.activeInHierarchy && IsInActiveScene(body);
        }

        private static bool IsCameraUsable(Camera cam)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                return false;
            }
            if (!IsInActiveScene(cam.transform))
            {
                return false;
            }
            return !IsMapCamera(cam) && !IsMenuCamera(cam);
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

            if (HasAnyComponent(cam.gameObject, PlayerCameraComponentNames))
            {
                return true;
            }

            return HasAnyComponentOnParents(cam.transform, PlayerCameraComponentNames) ||
                   HasAnyComponentOnParents(cam.transform, PlayerBodyComponentNames);
        }

        private static bool IsLikelyPlayerTransform(Transform body, Transform view)
        {
            if (body != null)
            {
                if (body.GetComponent<CharacterController>() != null)
                {
                    return true;
                }
                if (HasAnyComponent(body.gameObject, PlayerBodyComponentNames))
                {
                    return true;
                }
                if (HasAnyComponentOnParents(body, PlayerBodyComponentNames))
                {
                    return true;
                }
            }

            if (view != null)
            {
                if (HasAnyComponentOnParents(view, PlayerCameraComponentNames))
                {
                    return true;
                }
                if (HasAnyComponentOnParents(view, PlayerBodyComponentNames))
                {
                    return true;
                }
            }

            return false;
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

        private static bool HasAnyComponentOnParents(Transform transform, string[] typeNames)
        {
            Transform current = transform;
            int depth = 0;
            while (current != null && depth < 6)
            {
                GameObject obj = current.gameObject;
                if (HasAnyComponent(obj, typeNames))
                {
                    return true;
                }
                current = current.parent;
                depth++;
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

        private static bool IsMenuCamera(Camera cam)
        {
            if (cam == null)
            {
                return false;
            }

            if (IsMainMenuSceneName(Application.loadedLevelName))
            {
                return true;
            }

            Transform current = cam.transform;
            int depth = 0;
            while (current != null && depth < 6)
            {
                string name = current.name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.IndexOf("GUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("MENU", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                if (current.GetComponent("StartGame") != null || current.GetComponent("SettingsMenu") != null)
                {
                    return true;
                }
                current = current.parent;
                depth++;
            }

            return false;
        }

        private static bool IsMainMenuSceneName(string levelName)
        {
            if (string.IsNullOrEmpty(levelName))
            {
                return false;
            }
            string normalized = levelName.Replace(" ", string.Empty);
            return string.Equals(normalized, "MainMenu", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInActiveScene(Transform transform)
        {
            if (transform == null || transform.gameObject == null)
            {
                return false;
            }

            string activeName = Application.loadedLevelName ?? string.Empty;
            if (string.IsNullOrEmpty(activeName))
            {
                return true;
            }

            string sceneName = TryGetSceneName(transform.gameObject);
            if (string.IsNullOrEmpty(sceneName))
            {
                return true;
            }

            if (string.Equals(sceneName, activeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(sceneName, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase))
            {
                if (HasAnyComponent(transform.gameObject, PlayerBodyComponentNames) ||
                    HasAnyComponent(transform.gameObject, PlayerCameraComponentNames))
                {
                    return true;
                }
            }

            return false;
        }

        private static string TryGetSceneName(GameObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            try
            {
                PropertyInfo sceneProp = obj.GetType().GetProperty("scene");
                if (sceneProp == null)
                {
                    return string.Empty;
                }

                object scene = sceneProp.GetValue(obj, null);
                if (scene == null)
                {
                    return string.Empty;
                }

                PropertyInfo nameProp = scene.GetType().GetProperty("name");
                if (nameProp == null)
                {
                    return string.Empty;
                }

                return nameProp.GetValue(scene, null) as string ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void LogFailure()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextFailureLogTime)
            {
                return;
            }
            _nextFailureLogTime = now + 2f;

            DebugLog.Warn("PlayerLocator: failed to resolve local player. Cameras=" + Camera.allCamerasCount);
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (cam == null)
                {
                    continue;
                }
                DebugLog.Verbose("Camera: " + cam.name + " enabled=" + cam.enabled + " active=" + cam.gameObject.activeInHierarchy);
            }
        }

        private static bool NameContains(string name, string needle)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(needle))
            {
                return false;
            }
            return name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static long GetUnixTimeMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

        private void Cache(Transform body, Transform view)
        {
            _cachedCamera = view != null ? view.GetComponent<Camera>() : null;
            _cachedBody = body;
            _cachedView = view;
        }

        public bool Warmup(string context)
        {
            PlayerStateData state;
            bool ok = TryGetLocalState(out state);
            if (ok)
            {
                DebugLog.Verbose("PlayerLocator: warmup success (" + context + ") body=" + BuildPath(_cachedBody) + " view=" + BuildPath(_cachedView));
            }
            return ok;
        }

        private void CheckForLevelChange()
        {
            int levelIndex = Application.loadedLevel;
            string levelName = Application.loadedLevelName ?? string.Empty;
            if (levelIndex == _cachedLevelIndex && string.Equals(levelName, _cachedLevelName, StringComparison.Ordinal))
            {
                return;
            }

            _cachedLevelIndex = levelIndex;
            _cachedLevelName = levelName;
            _cachedCamera = null;
            _cachedBody = null;
            _cachedView = null;
        }

        private void LogResolve(Transform body, Transform view, string reason)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextResolveLogTime)
            {
                return;
            }
            _nextResolveLogTime = now + 2f;

            string bodyPath = BuildPath(body);
            string viewPath = BuildPath(view);
            DebugLog.Verbose("PlayerLocator: resolved (" + reason + ") body=" + bodyPath + " view=" + viewPath);
        }

        private static string BuildPath(Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            System.Collections.Generic.Stack<string> parts = new System.Collections.Generic.Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Push(current.name + "#" + current.GetSiblingIndex());
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private static bool TryResolveStandardController(out Transform body, out Transform view)
        {
            body = null;
            view = null;

            for (int i = 0; i < StandardControllerTypeNames.Length; i++)
            {
                Component controller = FindComponentByTypeName(StandardControllerTypeNames[i]);
                if (controller == null)
                {
                    continue;
                }

                body = controller.transform;
                Camera cam = TryGetCameraField(controller, "cam") ?? TryGetCameraField(controller, "m_Camera");
                if (cam == null)
                {
                    cam = controller.GetComponentInChildren<Camera>();
                }
                view = cam != null ? cam.transform : null;
                if (body != null && !IsInActiveScene(body))
                {
                    body = null;
                }
                if (view != null && !IsInActiveScene(view))
                {
                    view = null;
                }
                return body != null || view != null;
            }

            return false;
        }

        private static Component FindComponentByTypeName(string typeName)
        {
            Type type = FindType(typeName);
            if (type == null)
            {
                return null;
            }
            return UnityEngine.Object.FindObjectOfType(type) as Component;
        }

        private static Type FindType(string typeName)
        {
            Type type = Type.GetType(typeName);
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static Camera TryGetCameraField(Component component, string fieldName)
        {
            if (component == null || string.IsNullOrEmpty(fieldName))
            {
                return null;
            }

            FieldInfo field = component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || !typeof(Camera).IsAssignableFrom(field.FieldType))
            {
                return null;
            }
            return field.GetValue(component) as Camera;
        }
    }
}
