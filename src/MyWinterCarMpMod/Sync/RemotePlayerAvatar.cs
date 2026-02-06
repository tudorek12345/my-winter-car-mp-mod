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
        private Transform _modelRoot;
        private Vector3 _modelBaseLocalPosition;
        private Quaternion _modelBaseLocalRotation = Quaternion.identity;
        private bool _seated;
        private Vector3 _seatOffset;
        private Quaternion _seatRotation = Quaternion.identity;
        private Vector3 _walkOffset;
        private Quaternion _walkRotation = Quaternion.identity;
        private float _walkPhase;
        private float _lastWalkTime;
        private Vector3 _lastTargetPos;
        private bool _hasLastTargetPos;
        private float _smoothedWalkSpeed;
        private LocomotionRig _locomotionRig;

        public RemotePlayerAvatar(Settings settings)
        {
            _settings = settings;
        }

        public void Update(PlayerStateData state, bool hasState, bool allowApply, float positionSmoothing, float rotationSmoothing, bool isSeated)
        {
            if (!allowApply || !hasState)
            {
                return;
            }

            EnsureAvatar();

            Vector3 targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
            Quaternion viewRot = new Quaternion(state.ViewRotX, state.ViewRotY, state.ViewRotZ, state.ViewRotW);
            Quaternion targetRot = YawFrom(viewRot);

            UpdateSeatPose(isSeated);
            UpdateWalkPose(targetPos, isSeated);

            if (state.UnixTimeMs > 0 && state.UnixTimeMs <= _lastStateTimeMs)
            {
                return;
            }

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
            _modelRoot = null;
            _seated = false;
            _seatOffset = Vector3.zero;
            _seatRotation = Quaternion.identity;
            _walkOffset = Vector3.zero;
            _walkRotation = Quaternion.identity;
            _walkPhase = 0f;
            _lastWalkTime = 0f;
            _hasLastTargetPos = false;
            _smoothedWalkSpeed = 0f;
            _locomotionRig = null;
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

            _modelRoot = modelRoot;
            _modelBaseLocalPosition = modelRoot.localPosition;
            _modelBaseLocalRotation = modelRoot.localRotation;
            _locomotionRig = BuildLocomotionRig(modelRoot);

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

        private void UpdateSeatPose(bool seated)
        {
            if (_modelRoot == null)
            {
                return;
            }

            if (seated == _seated)
            {
                return;
            }

            _seated = seated;
            if (seated)
            {
                _seatOffset = new Vector3(0f, -0.75f, -0.12f);
                _seatRotation = Quaternion.Euler(5f, 0f, 0f);
            }
            else
            {
                _seatOffset = Vector3.zero;
                _seatRotation = Quaternion.identity;
            }
            ApplyModelOffsets();
        }

        private void UpdateWalkPose(Vector3 targetPos, bool seated)
        {
            if (_modelRoot == null)
            {
                return;
            }

            if (seated)
            {
                _walkOffset = Vector3.zero;
                _walkRotation = Quaternion.identity;
                _walkPhase = 0f;
                _hasLastTargetPos = false;
                _smoothedWalkSpeed = 0f;
                ApplyLocomotionRig(0f);
                ApplyModelOffsets();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (!_hasLastTargetPos)
            {
                _lastTargetPos = targetPos;
                _lastWalkTime = now;
                _hasLastTargetPos = true;
                _walkOffset = Vector3.zero;
                _walkRotation = Quaternion.identity;
                _smoothedWalkSpeed = 0f;
                ApplyLocomotionRig(0f);
                ApplyModelOffsets();
                return;
            }

            float delta = Mathf.Max(0.001f, now - _lastWalkTime);
            Vector3 deltaPos = targetPos - _lastTargetPos;
            float speed = deltaPos.magnitude / delta;
            _lastTargetPos = targetPos;
            _lastWalkTime = now;

            _smoothedWalkSpeed = Mathf.Lerp(_smoothedWalkSpeed, speed, Clamp01(delta * 10f));
            float stride = Mathf.Clamp01(_smoothedWalkSpeed / 2.8f);
            bool moving = stride > 0.05f;

            if (moving)
            {
                float phaseSpeed = Mathf.Lerp(3f, 8f, stride);
                _walkPhase += delta * phaseSpeed;
                float bob = Mathf.Sin(_walkPhase * 2f) * 0.03f * stride;
                float sway = Mathf.Sin(_walkPhase) * 0.015f * stride;
                float roll = Mathf.Sin(_walkPhase * 2f) * 2.5f * stride;
                float yaw = 0f;
                float sideLean = 0f;
                if (deltaPos.sqrMagnitude > 0.0001f && _avatar != null)
                {
                    Vector3 localDir = _avatar.transform.InverseTransformDirection(deltaPos.normalized);
                    yaw = Mathf.Clamp(localDir.x, -1f, 1f) * 2f * stride;
                    sideLean = Mathf.Clamp(localDir.x, -1f, 1f) * 4f * stride;
                }
                _walkOffset = new Vector3(sway, bob, 0f);
                _walkRotation = Quaternion.Euler(0f, yaw, roll + sideLean);
            }
            else
            {
                _walkPhase = Mathf.Lerp(_walkPhase, 0f, Clamp01(delta * 6f));
                _walkOffset = Vector3.zero;
                _walkRotation = Quaternion.identity;
            }

            ApplyLocomotionRig(stride);
            ApplyModelOffsets();
        }

        private void ApplyLocomotionRig(float stride)
        {
            if (_locomotionRig == null)
            {
                return;
            }

            float cycle = _walkPhase;
            float legSwing = Mathf.Sin(cycle) * 30f * stride;
            float legSwingOpp = Mathf.Sin(cycle + Mathf.PI) * 30f * stride;
            float kneeLeft = Mathf.Max(0f, -Mathf.Sin(cycle)) * 32f * stride;
            float kneeRight = Mathf.Max(0f, -Mathf.Sin(cycle + Mathf.PI)) * 32f * stride;
            float footLeft = (-legSwing * 0.35f) + (kneeLeft * 0.35f);
            float footRight = (-legSwingOpp * 0.35f) + (kneeRight * 0.35f);
            float armLeft = -legSwing * 0.65f;
            float armRight = -legSwingOpp * 0.65f;
            float forearmLeft = Mathf.Max(0f, legSwing) * 10f * stride;
            float forearmRight = Mathf.Max(0f, legSwingOpp) * 10f * stride;
            float hipsRoll = Mathf.Sin(cycle * 2f) * 3f * stride;
            float torsoPitch = Mathf.Sin(cycle + (Mathf.PI * 0.5f)) * 2f * stride;

            ApplyBoneRotation(_locomotionRig.Hips, _locomotionRig.HipsBaseRot, 0f, 0f, hipsRoll);
            ApplyBoneRotation(_locomotionRig.Spine, _locomotionRig.SpineBaseRot, torsoPitch, 0f, -hipsRoll * 0.5f);
            ApplyBoneRotation(_locomotionRig.Chest, _locomotionRig.ChestBaseRot, torsoPitch * 0.6f, 0f, -hipsRoll * 0.3f);
            ApplyBoneRotation(_locomotionRig.Head, _locomotionRig.HeadBaseRot, -torsoPitch * 0.3f, 0f, -hipsRoll * 0.2f);

            ApplyBoneRotation(_locomotionRig.UpperLegL, _locomotionRig.UpperLegLBaseRot, legSwing, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.UpperLegR, _locomotionRig.UpperLegRBaseRot, legSwingOpp, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.LowerLegL, _locomotionRig.LowerLegLBaseRot, kneeLeft, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.LowerLegR, _locomotionRig.LowerLegRBaseRot, kneeRight, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.FootL, _locomotionRig.FootLBaseRot, footLeft, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.FootR, _locomotionRig.FootRBaseRot, footRight, 0f, 0f);

            ApplyBoneRotation(_locomotionRig.UpperArmL, _locomotionRig.UpperArmLBaseRot, armLeft, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.UpperArmR, _locomotionRig.UpperArmRBaseRot, armRight, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.ForeArmL, _locomotionRig.ForeArmLBaseRot, forearmLeft, 0f, 0f);
            ApplyBoneRotation(_locomotionRig.ForeArmR, _locomotionRig.ForeArmRBaseRot, forearmRight, 0f, 0f);
        }

        private static void ApplyBoneRotation(Transform bone, Quaternion baseRotation, float x, float y, float z)
        {
            if (bone == null)
            {
                return;
            }

            bone.localRotation = baseRotation * Quaternion.Euler(x, y, z);
        }

        private static LocomotionRig BuildLocomotionRig(Transform modelRoot)
        {
            if (modelRoot == null)
            {
                return null;
            }

            Transform[] bones = modelRoot.GetComponentsInChildren<Transform>(true);
            if (bones == null || bones.Length == 0)
            {
                return null;
            }

            LocomotionRig rig = new LocomotionRig();
            rig.Hips = FindBoneByTokens(bones, new[] { "hips", "pelvis", "hip" });
            rig.Spine = FindBoneByTokens(bones, new[] { "spine", "spine1", "torso" });
            rig.Chest = FindBoneByTokens(bones, new[] { "chest", "spine2", "upperchest" });
            rig.Head = FindBoneByTokens(bones, new[] { "head", "neck" });

            rig.UpperLegL = FindBoneBySideAndPartTokens(bones, new[] { "left", " l ", "_l", ".l", "l_" }, new[] { "thigh", "upleg", "upperleg", "leg" });
            rig.UpperLegR = FindBoneBySideAndPartTokens(bones, new[] { "right", " r ", "_r", ".r", "r_" }, new[] { "thigh", "upleg", "upperleg", "leg" });
            rig.LowerLegL = FindBoneBySideAndPartTokens(bones, new[] { "left", " l ", "_l", ".l", "l_" }, new[] { "calf", "shin", "knee", "lowerleg", "leg" });
            rig.LowerLegR = FindBoneBySideAndPartTokens(bones, new[] { "right", " r ", "_r", ".r", "r_" }, new[] { "calf", "shin", "knee", "lowerleg", "leg" });
            rig.FootL = FindBoneBySideAndPartTokens(bones, new[] { "left", " l ", "_l", ".l", "l_" }, new[] { "foot", "ankle" });
            rig.FootR = FindBoneBySideAndPartTokens(bones, new[] { "right", " r ", "_r", ".r", "r_" }, new[] { "foot", "ankle" });
            rig.UpperArmL = FindBoneBySideAndPartTokens(bones, new[] { "left", " l ", "_l", ".l", "l_" }, new[] { "upperarm", "arm", "shoulder" });
            rig.UpperArmR = FindBoneBySideAndPartTokens(bones, new[] { "right", " r ", "_r", ".r", "r_" }, new[] { "upperarm", "arm", "shoulder" });
            rig.ForeArmL = FindBoneBySideAndPartTokens(bones, new[] { "left", " l ", "_l", ".l", "l_" }, new[] { "forearm", "lowerarm", "elbow", "arm" });
            rig.ForeArmR = FindBoneBySideAndPartTokens(bones, new[] { "right", " r ", "_r", ".r", "r_" }, new[] { "forearm", "lowerarm", "elbow", "arm" });

            rig.HipsBaseRot = rig.Hips != null ? rig.Hips.localRotation : Quaternion.identity;
            rig.SpineBaseRot = rig.Spine != null ? rig.Spine.localRotation : Quaternion.identity;
            rig.ChestBaseRot = rig.Chest != null ? rig.Chest.localRotation : Quaternion.identity;
            rig.HeadBaseRot = rig.Head != null ? rig.Head.localRotation : Quaternion.identity;
            rig.UpperLegLBaseRot = rig.UpperLegL != null ? rig.UpperLegL.localRotation : Quaternion.identity;
            rig.UpperLegRBaseRot = rig.UpperLegR != null ? rig.UpperLegR.localRotation : Quaternion.identity;
            rig.LowerLegLBaseRot = rig.LowerLegL != null ? rig.LowerLegL.localRotation : Quaternion.identity;
            rig.LowerLegRBaseRot = rig.LowerLegR != null ? rig.LowerLegR.localRotation : Quaternion.identity;
            rig.FootLBaseRot = rig.FootL != null ? rig.FootL.localRotation : Quaternion.identity;
            rig.FootRBaseRot = rig.FootR != null ? rig.FootR.localRotation : Quaternion.identity;
            rig.UpperArmLBaseRot = rig.UpperArmL != null ? rig.UpperArmL.localRotation : Quaternion.identity;
            rig.UpperArmRBaseRot = rig.UpperArmR != null ? rig.UpperArmR.localRotation : Quaternion.identity;
            rig.ForeArmLBaseRot = rig.ForeArmL != null ? rig.ForeArmL.localRotation : Quaternion.identity;
            rig.ForeArmRBaseRot = rig.ForeArmR != null ? rig.ForeArmR.localRotation : Quaternion.identity;

            if (!rig.HasAnyLimb())
            {
                return null;
            }

            return rig;
        }

        private static Transform FindBoneByTokens(Transform[] bones, string[] tokens)
        {
            if (bones == null || tokens == null || tokens.Length == 0)
            {
                return null;
            }

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null || string.IsNullOrEmpty(bone.name))
                {
                    continue;
                }

                string lower = bone.name.ToLowerInvariant();
                for (int t = 0; t < tokens.Length; t++)
                {
                    string token = tokens[t];
                    if (string.IsNullOrEmpty(token))
                    {
                        continue;
                    }

                    if (lower.Contains(token))
                    {
                        return bone;
                    }
                }
            }

            return null;
        }

        private static Transform FindBoneBySideAndPartTokens(Transform[] bones, string[] sideTokens, string[] partTokens)
        {
            if (bones == null || sideTokens == null || sideTokens.Length == 0 || partTokens == null || partTokens.Length == 0)
            {
                return null;
            }

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null || string.IsNullOrEmpty(bone.name))
                {
                    continue;
                }

                string lower = bone.name.ToLowerInvariant();
                bool sideMatch = false;
                for (int s = 0; s < sideTokens.Length; s++)
                {
                    string token = sideTokens[s];
                    if (!string.IsNullOrEmpty(token) && lower.Contains(token))
                    {
                        sideMatch = true;
                        break;
                    }
                }
                if (!sideMatch)
                {
                    continue;
                }

                for (int p = 0; p < partTokens.Length; p++)
                {
                    string token = partTokens[p];
                    if (!string.IsNullOrEmpty(token) && lower.Contains(token))
                    {
                        return bone;
                    }
                }
            }

            return null;
        }

        private void ApplyModelOffsets()
        {
            if (_modelRoot == null)
            {
                return;
            }

            _modelRoot.localPosition = _modelBaseLocalPosition + _seatOffset + _walkOffset;
            _modelRoot.localRotation = _modelBaseLocalRotation * _seatRotation * _walkRotation;
        }

        private sealed class LocomotionRig
        {
            public Transform Hips;
            public Transform Spine;
            public Transform Chest;
            public Transform Head;
            public Transform UpperLegL;
            public Transform UpperLegR;
            public Transform LowerLegL;
            public Transform LowerLegR;
            public Transform FootL;
            public Transform FootR;
            public Transform UpperArmL;
            public Transform UpperArmR;
            public Transform ForeArmL;
            public Transform ForeArmR;

            public Quaternion HipsBaseRot = Quaternion.identity;
            public Quaternion SpineBaseRot = Quaternion.identity;
            public Quaternion ChestBaseRot = Quaternion.identity;
            public Quaternion HeadBaseRot = Quaternion.identity;
            public Quaternion UpperLegLBaseRot = Quaternion.identity;
            public Quaternion UpperLegRBaseRot = Quaternion.identity;
            public Quaternion LowerLegLBaseRot = Quaternion.identity;
            public Quaternion LowerLegRBaseRot = Quaternion.identity;
            public Quaternion FootLBaseRot = Quaternion.identity;
            public Quaternion FootRBaseRot = Quaternion.identity;
            public Quaternion UpperArmLBaseRot = Quaternion.identity;
            public Quaternion UpperArmRBaseRot = Quaternion.identity;
            public Quaternion ForeArmLBaseRot = Quaternion.identity;
            public Quaternion ForeArmRBaseRot = Quaternion.identity;

            public bool HasAnyLimb()
            {
                return UpperLegL != null || UpperLegR != null ||
                    LowerLegL != null || LowerLegR != null ||
                    FootL != null || FootR != null ||
                    UpperArmL != null || UpperArmR != null;
            }
        }
    }
}
