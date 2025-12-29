using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MWCSpectatorSync.Sync
{
    public sealed class LevelSync : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private Action<int, string> _hostLevelChanged;
        private bool _useSceneManager;
        private bool _isLoading;
        private int _currentLevelIndex = -1;
        private string _currentLevelName = string.Empty;
        private int _targetLevelIndex = -1;
        private string _targetLevelName = string.Empty;

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
            try
            {
                _useSceneManager = true;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                Scene active = SceneManager.GetActiveScene();
                UpdateCurrent(active.buildIndex, active.name);
            }
            catch (Exception)
            {
                _useSceneManager = false;
                UpdateCurrent(Application.loadedLevel, Application.loadedLevelName);
            }
        }

        public void Update()
        {
            if (_useSceneManager)
            {
                return;
            }

            int levelIndex = Application.loadedLevel;
            string levelName = Application.loadedLevelName;
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
                return;
            }

            _isLoading = true;
            if (_useSceneManager)
            {
                if (!string.IsNullOrEmpty(levelName))
                {
                    SceneManager.LoadSceneAsync(levelName);
                }
                else
                {
                    SceneManager.LoadSceneAsync(levelIndex);
                }
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
                _log.LogInfo("Spectator loading level " + levelIndex + " (" + levelName + ").");
            }
        }

        public void Dispose()
        {
            if (_useSceneManager)
            {
                SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            }
        }

        private void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            UpdateCurrent(newScene.buildIndex, newScene.name);
        }

        private void UpdateCurrent(int levelIndex, string levelName)
        {
            _currentLevelIndex = levelIndex;
            _currentLevelName = levelName ?? string.Empty;

            if (_hostLevelChanged != null)
            {
                _hostLevelChanged(levelIndex, levelName);
            }

            if (_isLoading && MatchesTarget(levelIndex, levelName))
            {
                _isLoading = false;
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
