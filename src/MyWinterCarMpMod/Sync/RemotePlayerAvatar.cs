using System.IO;
using BepInEx;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class RemotePlayerAvatar
    {
        private readonly Settings _settings;
        private GameObject _avatar;
        private bool _initialized;
        private long _lastStateTimeMs;
        private Renderer[] _renderers;
        private AssetBundle _bundle;
        private bool _bundleFailed;

        public RemotePlayerAvatar(Settings settings)
        {
            _settings = settings;
        }

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

            Transform modelRoot = new GameObject("AvatarModel").transform;
            modelRoot.SetParent(_avatar.transform, false);

            bool meshLoaded = TryCreateMeshAvatar(modelRoot);
            if (!meshLoaded)
            {
                CreatePrimitiveChild(modelRoot, "Body", PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.9f, 0.45f));
                CreatePrimitiveChild(modelRoot, "Head", PrimitiveType.Sphere, new Vector3(0f, 1.55f, 0f), new Vector3(0.3f, 0.3f, 0.3f));
                CreatePrimitiveChild(modelRoot, "Hip", PrimitiveType.Cube, new Vector3(0f, 0.55f, 0f), new Vector3(0.35f, 0.2f, 0.25f));
                CreatePrimitiveChild(modelRoot, "ArmL", PrimitiveType.Cube, new Vector3(-0.3f, 1.1f, 0f), new Vector3(0.15f, 0.45f, 0.15f));
                CreatePrimitiveChild(modelRoot, "ArmR", PrimitiveType.Cube, new Vector3(0.3f, 1.1f, 0f), new Vector3(0.15f, 0.45f, 0.15f));
                ApplyAvatarTransform(modelRoot);
            }

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

        private bool TryCreateMeshAvatar(Transform parent)
        {
            if (_settings == null)
            {
                return false;
            }

            string bundlePath = ResolveBundlePath(_settings.RemoteAvatarBundlePath.Value);
            string assetName = _settings.RemoteAvatarAssetName.Value;
            if (string.IsNullOrEmpty(bundlePath) || string.IsNullOrEmpty(assetName))
            {
                return false;
            }

            if (_bundle == null && !_bundleFailed)
            {
                if (!File.Exists(bundlePath))
                {
                    DebugLog.Warn("Remote avatar bundle not found: " + bundlePath);
                    _bundleFailed = true;
                    return false;
                }

                _bundle = AssetBundle.CreateFromFile(bundlePath);
                if (_bundle == null)
                {
                    DebugLog.Warn("Remote avatar bundle failed to load: " + bundlePath);
                    _bundleFailed = true;
                    return false;
                }
            }

            if (_bundle == null)
            {
                return false;
            }

            GameObject prefab = _bundle.LoadAsset<GameObject>(assetName);
            if (prefab != null)
            {
                GameObject instance = Object.Instantiate(prefab);
                instance.name = "MeshAvatar";
                instance.transform.SetParent(parent, false);
                ApplyAvatarTransform(instance.transform);
                DisableColliders(instance);
                return true;
            }

            Mesh mesh = _bundle.LoadAsset<Mesh>(assetName);
            if (mesh != null)
            {
                GameObject instance = new GameObject("MeshAvatar");
                instance.transform.SetParent(parent, false);
                ApplyAvatarTransform(instance.transform);
                MeshFilter filter = instance.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = instance.AddComponent<MeshRenderer>();
                Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
                if (shader != null)
                {
                    renderer.sharedMaterial = new Material(shader);
                }
                return true;
            }

            DebugLog.Warn("Remote avatar asset not found in bundle: " + assetName);
            _bundleFailed = true;
            return false;
        }

        private static string ResolveBundlePath(string bundlePath)
        {
            if (string.IsNullOrEmpty(bundlePath))
            {
                return bundlePath;
            }

            if (Path.IsPathRooted(bundlePath))
            {
                return bundlePath;
            }

            string bepInExRoot = Paths.BepInExRootPath;
            if (!string.IsNullOrEmpty(bepInExRoot))
            {
                return Path.GetFullPath(Path.Combine(bepInExRoot, bundlePath));
            }

            string dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string gameRoot = Path.GetDirectoryName(dataPath);
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    return Path.GetFullPath(Path.Combine(gameRoot, bundlePath));
                }
            }

            return bundlePath;
        }

        private void ApplyAvatarTransform(Transform transform)
        {
            float scale = _settings != null ? Mathf.Max(0.01f, _settings.RemoteAvatarScale.Value) : 1f;
            float yOffset = _settings != null ? _settings.RemoteAvatarYOffset.Value : 0f;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one * scale;
            transform.localPosition = Vector3.zero;

            float autoOffset = ComputeAutoYOffset(transform);
            float finalYOffset = yOffset + autoOffset;
            DebugLog.Info("Avatar transform scale=" + scale + " yOffset=" + yOffset + " autoYOffset=" + autoOffset + " finalYOffset=" + finalYOffset);
            transform.localPosition = new Vector3(0f, finalYOffset, 0f);
        }

        private static float ComputeAutoYOffset(Transform root)
        {
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            bool found = false;

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null)
                {
                    continue;
                }
                UpdateMeshBounds(root, filter.transform, filter.sharedMesh, ref minY, ref maxY, ref found);
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinned[i];
                if (renderer == null)
                {
                    continue;
                }
                UpdateMeshBounds(root, renderer.transform, renderer.sharedMesh, ref minY, ref maxY, ref found);
            }

            if (!found)
            {
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }
                    UpdateRendererBounds(root, renderer, ref minY, ref maxY, ref found);
                }
            }

            if (!found)
            {
                return 0f;
            }

            const float footPad = 0.06f;
            const float extraPad = 0.02f;
            float autoOffset = -minY + footPad + extraPad;
            DebugLog.Info("Avatar mesh bounds minY=" + minY + " maxY=" + maxY + " height=" + (maxY - minY) +
                " footPad=" + footPad + " extraPad=" + extraPad + " autoYOffset=" + autoOffset);
            return autoOffset;
        }

        private static void UpdateRendererBounds(Transform root, Renderer renderer, ref float minY, ref float maxY, ref bool found)
        {
            if (root == null || renderer == null)
            {
                return;
            }

            Bounds bounds = renderer.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = new Vector3[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 local = root.InverseTransformPoint(corners[i]);
                float y = local.y;
                if (y < minY)
                {
                    minY = y;
                }
                if (y > maxY)
                {
                    maxY = y;
                }
                found = true;
            }
        }

        private static void UpdateMeshBounds(Transform root, Transform meshTransform, Mesh mesh, ref float minY, ref float maxY, ref bool found)
        {
            if (root == null || meshTransform == null || mesh == null)
            {
                return;
            }

            Bounds bounds = mesh.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = new Vector3[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 world = meshTransform.TransformPoint(corners[i]);
                Vector3 local = root.InverseTransformPoint(world);
                float y = local.y;
                if (y < minY)
                {
                    minY = y;
                }
                if (y > maxY)
                {
                    maxY = y;
                }
                found = true;
            }
        }

        private static void DisableColliders(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }
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
                Material material = renderer.material;
                if (material != null && material.HasProperty("_Color"))
                {
                    material.color = color;
                }
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
