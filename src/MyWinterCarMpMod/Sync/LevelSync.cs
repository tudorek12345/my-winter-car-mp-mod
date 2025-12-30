using System;
using System.Reflection;
using BepInEx.Logging;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class LevelSync : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private Action<int, string> _hostLevelChanged;
        private bool _useSceneManager;
        private bool _isLoading;
        private SceneManagerProxy _sceneManager;
        private int _currentLevelIndex = -1;
        private string _currentLevelName = string.Empty;
        private int _targetLevelIndex = -1;
        private string _targetLevelName = string.Empty;
        private float _loadStartTime;
        private int _loadAttempts;
        private bool _loadByName;
        private bool _loadFailureLogged;
        private const float LoadTimeoutSeconds = 6f;

        public LevelSync(ManualLogSource log, bool verbose)
        {
            _log = log;
            _verbose = verbose;
        }

        public int CurrentLevelIndex
        {
            get { return _currentLevelIndex; }
        }

        public string CurrentLevelName
        {
            get { return _currentLevelName; }
        }

        public bool IsReady
        {
            get { return !_isLoading && (_targetLevelIndex < 0 || _currentLevelIndex == _targetLevelIndex); }
        }

        public void Initialize(Action<int, string> hostLevelChanged)
        {
            _hostLevelChanged = hostLevelChanged;
            _sceneManager = SceneManagerProxy.TryCreate();
            _useSceneManager = _sceneManager != null;

            int index;
            string name;
            if (_useSceneManager && _sceneManager.TryGetActiveScene(out index, out name))
            {
                UpdateCurrent(index, name);
            }
            else
            {
                _useSceneManager = false;
                UpdateCurrent(Application.loadedLevel, Application.loadedLevelName);
            }
        }

        public void Update()
        {
            int levelIndex;
            string levelName;
            if (_useSceneManager && _sceneManager != null && _sceneManager.TryGetActiveScene(out levelIndex, out levelName))
            {
                // Use scene manager when available.
            }
            else
            {
                levelIndex = Application.loadedLevel;
                levelName = Application.loadedLevelName;
            }

            if (_isLoading)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _loadStartTime > LoadTimeoutSeconds)
                {
                    if (_loadByName && _targetLevelIndex >= 0 && _loadAttempts == 1)
                    {
                        _loadAttempts++;
                        _loadStartTime = now;
                        _loadByName = false;
                        DebugLog.Warn("Level load timeout. Retrying by index " + _targetLevelIndex + " (name=" + _targetLevelName + ").");
                        Application.LoadLevel(_targetLevelIndex);
                    }
                    else if (!_loadFailureLogged)
                    {
                        _loadFailureLogged = true;
                        DebugLog.Warn("Level load still not complete. Target=" + _targetLevelName + " Index=" + _targetLevelIndex + " Current=" + levelName);
                    }
                }
            }

            if (levelIndex != _currentLevelIndex || levelName != _currentLevelName)
            {
                UpdateCurrent(levelIndex, levelName);
            }
        }

        public void ApplyLevelChange(int levelIndex, string levelName)
        {
            _targetLevelIndex = levelIndex;
            _targetLevelName = levelName ?? string.Empty;

            if (IsCurrentLevel(levelIndex, levelName))
            {
                _isLoading = false;
                DebugLog.Verbose("LevelChange ignored (already on target). Index=" + levelIndex + " Name=" + levelName);
                return;
            }

            _isLoading = true;
            _loadStartTime = Time.realtimeSinceStartup;
            _loadAttempts = 1;
            _loadByName = !string.IsNullOrEmpty(levelName);
            _loadFailureLogged = false;
            if (_useSceneManager)
            {
                _sceneManager.LoadSceneAsync(levelIndex, levelName);
            }
            else
            {
                if (!string.IsNullOrEmpty(levelName))
                {
                    Application.LoadLevel(levelName);
                }
                else
                {
                    Application.LoadLevel(levelIndex);
                }
            }

            if (_log != null)
            {
                _log.LogInfo("Client loading level " + levelIndex + " (" + levelName + ").");
            }
            DebugLog.Info("Client loading level. Index=" + levelIndex + " Name=" + levelName);
        }

        public void Dispose()
        {
        }

        private sealed class SceneManagerProxy
        {
            private readonly MethodInfo _getActiveScene;
            private readonly MethodInfo _loadSceneAsyncByName;
            private readonly MethodInfo _loadSceneAsyncByIndex;
            private PropertyInfo _sceneName;
            private PropertyInfo _sceneBuildIndex;

            private SceneManagerProxy(MethodInfo getActiveScene, MethodInfo loadByName, MethodInfo loadByIndex)
            {
                _getActiveScene = getActiveScene;
                _loadSceneAsyncByName = loadByName;
                _loadSceneAsyncByIndex = loadByIndex;
            }

            public static SceneManagerProxy TryCreate()
            {
                Type sceneManagerType = FindSceneManagerType();
                if (sceneManagerType == null)
                {
                    return null;
                }

                MethodInfo getActiveScene = sceneManagerType.GetMethod("GetActiveScene", BindingFlags.Public | BindingFlags.Static);
                MethodInfo loadByName = sceneManagerType.GetMethod("LoadSceneAsync", new[] { typeof(string) });
                MethodInfo loadByIndex = sceneManagerType.GetMethod("LoadSceneAsync", new[] { typeof(int) });
                if (getActiveScene == null || loadByName == null || loadByIndex == null)
                {
                    return null;
                }

                return new SceneManagerProxy(getActiveScene, loadByName, loadByIndex);
            }

            public bool TryGetActiveScene(out int buildIndex, out string name)
            {
                buildIndex = -1;
                name = string.Empty;

                object scene = _getActiveScene.Invoke(null, null);
                if (scene == null)
                {
                    return false;
                }

                if (_sceneName == null || _sceneBuildIndex == null)
                {
                    Type sceneType = scene.GetType();
                    _sceneName = sceneType.GetProperty("name");
                    _sceneBuildIndex = sceneType.GetProperty("buildIndex");
                }

                if (_sceneName == null || _sceneBuildIndex == null)
                {
                    return false;
                }

                name = _sceneName.GetValue(scene, null) as string ?? string.Empty;
                buildIndex = (int)_sceneBuildIndex.GetValue(scene, null);
                return true;
            }

            public void LoadSceneAsync(int levelIndex, string levelName)
            {
                if (!string.IsNullOrEmpty(levelName))
                {
                    _loadSceneAsyncByName.Invoke(null, new object[] { levelName });
                }
                else
                {
                    _loadSceneAsyncByIndex.Invoke(null, new object[] { levelIndex });
                }
            }

            private static Type FindSceneManagerType()
            {
                Type type = Type.GetType("UnityEngine.SceneManagement.SceneManager, UnityEngine");
                if (type != null)
                {
                    return type;
                }

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    type = assemblies[i].GetType("UnityEngine.SceneManagement.SceneManager", false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                return null;
            }
        }

        private void UpdateCurrent(int levelIndex, string levelName)
        {
            _currentLevelIndex = levelIndex;
            _currentLevelName = levelName ?? string.Empty;

            if (_hostLevelChanged != null)
            {
                _hostLevelChanged(levelIndex, levelName);
                DebugLog.Info("Host level changed. Index=" + levelIndex + " Name=" + levelName);
            }

            if (_isLoading && MatchesTarget(levelIndex, levelName))
            {
                _isLoading = false;
                DebugLog.Info("Level load complete. Index=" + _currentLevelIndex + " Name=" + _currentLevelName);
            }

            if (_verbose && _log != null)
            {
                _log.LogInfo("Active level: " + _currentLevelIndex + " " + _currentLevelName);
            }
        }

        private bool IsCurrentLevel(int levelIndex, string levelName)
        {
            if (levelIndex == _currentLevelIndex)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(levelName) && levelName == _currentLevelName)
            {
                return true;
            }
            return false;
        }

        private bool MatchesTarget(int levelIndex, string levelName)
        {
            if (_targetLevelIndex >= 0 && levelIndex == _targetLevelIndex)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(_targetLevelName) && levelName == _targetLevelName)
            {
                return true;
            }
            return false;
        }
    }
}
