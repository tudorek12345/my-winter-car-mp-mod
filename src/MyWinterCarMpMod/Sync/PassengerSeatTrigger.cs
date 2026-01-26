using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    internal sealed class PassengerSeatTrigger : MonoBehaviour
    {
        private static readonly string[] MovementComponentTokens = new[]
        {
            "CharacterMotor",
            "FPSInputController",
            "FirstPersonController",
            "RigidbodyFirstPersonController",
            "ThirdPersonController"
        };

        private VehicleSync _vehicleSync;
        private uint _vehicleId;
        private Transform _playerRoot;
        private Vector3 _seatedLocalPosition;
        private Quaternion _seatedLocalRotation;
        private bool _canSit;
        private bool _isSitting;
        private bool _showGui;
        private PlayMakerFSM _iconsFsm;
        private PlayMakerFSM _textFsm;
        private bool _guiReady;
        private readonly List<Behaviour> _disabledBehaviours = new List<Behaviour>();
        private readonly List<Collider> _disabledColliders = new List<Collider>();
        private readonly List<CharacterController> _disabledControllers = new List<CharacterController>();

        public void Initialize(VehicleSync vehicleSync, uint vehicleId)
        {
            _vehicleSync = vehicleSync;
            _vehicleId = vehicleId;
        }

        private void Start()
        {
            TryResolveGui();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsLocalPlayerCollider(other))
            {
                return;
            }

            _canSit = true;
            if (!_isSitting)
            {
                _showGui = true;
            }

            if (_playerRoot == null)
            {
                _playerRoot = ResolvePlayerRoot(other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsLocalPlayerCollider(other))
            {
                return;
            }

            _canSit = false;
            if (!_isSitting)
            {
                _showGui = false;
                UpdateGui(false);
            }
        }

        private void Update()
        {
            if (_showGui)
            {
                UpdateGui(true);
            }

            if (Input.GetKeyDown(KeyCode.Return))
            {
                if (!_isSitting && _canSit)
                {
                    EnterSeat();
                }
                else if (_isSitting)
                {
                    ExitSeat();
                }
            }

            if (_isSitting && _playerRoot != null)
            {
                _playerRoot.localPosition = _seatedLocalPosition;
                _playerRoot.localRotation = _seatedLocalRotation;
            }
        }

        private void EnterSeat()
        {
            Transform player = _playerRoot ?? FindPlayerFromScene();
            if (player == null)
            {
                DebugLog.Warn("PassengerSeat: local player not found.");
                return;
            }

            _playerRoot = player;
            DisablePlayerMovement(player);
            player.SetParent(transform, true);
            _seatedLocalPosition = player.localPosition;
            _seatedLocalRotation = player.localRotation;
            _isSitting = true;
            _showGui = false;
            UpdateGui(false);

            if (_vehicleSync != null)
            {
                _vehicleSync.NotifySeatEvent(_vehicleId, true, false);
            }
        }

        private void ExitSeat()
        {
            if (_playerRoot == null)
            {
                return;
            }

            _playerRoot.SetParent(null, true);
            RestorePlayerMovement();
            _isSitting = false;
            _showGui = _canSit;
            UpdateGui(_showGui);

            if (_vehicleSync != null)
            {
                _vehicleSync.NotifySeatEvent(_vehicleId, false, false);
            }
        }

        private void DisablePlayerMovement(Transform root)
        {
            _disabledBehaviours.Clear();
            _disabledColliders.Clear();
            _disabledControllers.Clear();

            if (root == null)
            {
                return;
            }

            Behaviour[] behaviours = root.GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled)
                {
                    continue;
                }

                if (IsMovementBehaviour(behaviour.GetType()))
                {
                    behaviour.enabled = false;
                    _disabledBehaviours.Add(behaviour);
                }
            }

            CharacterController[] controllers = root.GetComponentsInChildren<CharacterController>(true);
            for (int i = 0; i < controllers.Length; i++)
            {
                CharacterController controller = controllers[i];
                if (controller != null && controller.enabled)
                {
                    controller.enabled = false;
                    _disabledControllers.Add(controller);
                }
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled || collider.isTrigger)
                {
                    continue;
                }

                collider.enabled = false;
                _disabledColliders.Add(collider);
            }
        }

        private void RestorePlayerMovement()
        {
            for (int i = 0; i < _disabledBehaviours.Count; i++)
            {
                Behaviour behaviour = _disabledBehaviours[i];
                if (behaviour != null)
                {
                    behaviour.enabled = true;
                }
            }
            for (int i = 0; i < _disabledControllers.Count; i++)
            {
                CharacterController controller = _disabledControllers[i];
                if (controller != null)
                {
                    controller.enabled = true;
                }
            }
            for (int i = 0; i < _disabledColliders.Count; i++)
            {
                Collider collider = _disabledColliders[i];
                if (collider != null)
                {
                    collider.enabled = true;
                }
            }
            _disabledBehaviours.Clear();
            _disabledControllers.Clear();
            _disabledColliders.Clear();
        }

        private void TryResolveGui()
        {
            GameObject gui = GameObject.Find("GUI");
            if (gui == null)
            {
                return;
            }

            PlayMakerFSM[] fsms = gui.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null)
                {
                    continue;
                }

                if (string.Equals(fsm.FsmName, "Logic", StringComparison.OrdinalIgnoreCase))
                {
                    _iconsFsm = fsm;
                }
                else if (string.Equals(fsm.FsmName, "SetText", StringComparison.OrdinalIgnoreCase) &&
                    fsm.gameObject != null &&
                    string.Equals(fsm.gameObject.name, "Interaction", StringComparison.OrdinalIgnoreCase))
                {
                    _textFsm = fsm;
                }
            }

            _guiReady = _iconsFsm != null && _textFsm != null;
        }

        private void UpdateGui(bool show)
        {
            if (!_guiReady)
            {
                return;
            }

            try
            {
                _iconsFsm.Fsm.GetFsmBool("GUIpassenger").Value = show;
                _textFsm.Fsm.GetFsmString("GUIinteraction").Value = show ? "ENTER PASSENGER MODE" : string.Empty;
            }
            catch (Exception)
            {
                _guiReady = false;
            }
        }

        private static bool IsLocalPlayerCollider(Collider collider)
        {
            if (collider == null)
            {
                return false;
            }

            Transform root = collider.transform != null ? collider.transform.root : null;
            if (root != null && NameContains(root.name, "player"))
            {
                return true;
            }

            try
            {
                if (collider.CompareTag("Player") || (root != null && root.CompareTag("Player")))
                {
                    return true;
                }
            }
            catch (UnityException)
            {
            }

            return false;
        }

        private static Transform ResolvePlayerRoot(Collider collider)
        {
            if (collider == null)
            {
                return null;
            }

            CharacterController controller = collider.GetComponentInParent<CharacterController>();
            if (controller != null)
            {
                return controller.transform;
            }

            Transform current = collider.transform;
            while (current != null)
            {
                if (NameContains(current.name, "player"))
                {
                    return current;
                }
                current = current.parent;
            }

            return collider.transform.root;
        }

        private static Transform FindPlayerFromScene()
        {
            GameObject player = GameObject.Find("PLAYER") ?? GameObject.Find("Player");
            return player != null ? player.transform : null;
        }

        private static bool IsMovementBehaviour(Type type)
        {
            if (type == null)
            {
                return false;
            }

            string name = type.Name ?? string.Empty;
            string fullName = type.FullName ?? string.Empty;
            for (int i = 0; i < MovementComponentTokens.Length; i++)
            {
                string token = MovementComponentTokens[i];
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fullName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NameContains(string name, string token)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(token))
            {
                return false;
            }

            return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
