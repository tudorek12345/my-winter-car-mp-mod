using UnityEngine;

namespace MyWinterCarMpMod.Sync
{
    public sealed class MPCarController : CarController
    {
        private float _throttleInput;
        private float _brakeInput;
        private float _steerInput;
        private float _handbrakeInput;
        private float _clutchInput;
        private bool _startEngineQueued;
        private int _targetGear;
        private uint _lastSequence;
        private float _lastUpdateTime;

        public uint LastSequence
        {
            get { return _lastSequence; }
        }

        public float LastUpdateTime
        {
            get { return _lastUpdateTime; }
        }

        public void SetInput(float throttle, float brake, float steer, float handbrake, float clutch, bool startEngine, int targetGear, uint sequence, float now)
        {
            _throttleInput = throttle;
            _brakeInput = brake;
            _steerInput = steer;
            _handbrakeInput = handbrake;
            _clutchInput = clutch;
            if (startEngine)
            {
                _startEngineQueued = true;
            }
            _targetGear = targetGear;
            _lastSequence = sequence;
            _lastUpdateTime = now;
        }

        protected override void GetInput(out float throttleInput, out float brakeInput, out float steerInput, out float handbrakeInput, out float clutchInput, out bool startEngineInput, out int targetGear)
        {
            throttleInput = _throttleInput;
            brakeInput = _brakeInput;
            steerInput = _steerInput;
            handbrakeInput = _handbrakeInput;
            clutchInput = _clutchInput;
            startEngineInput = _startEngineQueued;
            _startEngineQueued = false;
            targetGear = _targetGear;
        }
    }
}
