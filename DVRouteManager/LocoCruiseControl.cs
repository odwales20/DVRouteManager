using CommandTerminal;
using DV.HUD;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DVRouteManager
{
    public class LocoCruiseControl : IDisposable
    {
        protected ILocomotiveRemoteControl remoteControl;
        protected TrainCar trainCar;

        public float TargetSpeed { get; protected set; } = 20.0f;
        protected bool running;

        private static LocoCruiseControl CruiseControl;

        public static bool IsSet { get => CruiseControl != null && CruiseControl.Running; }
        protected bool Running { get => running; }

        // ── DM3 gear state ──────────────────────────────────────────────────
        private const string LOCO_DM3 = "LocoDM3";
        private const float RPM_SHIFT_UP   = 800f;
        private const float RPM_SHIFT_DOWN = 600f;
        private const float SHIFT_COOLDOWN = 3.0f; // seconds between shifts
        private const float DM3_MAX_SPEED  = 65f;  // km/h, aligned with SteamCruiseControl default

        // DriverAssist-style protection defaults. These are deliberately hard-coded for
        // the AI controller so it remains independent from the DriverAssist mod.
        private const float THROTTLE_NOTCH = 1f / 11f;
        private const float PROTECTION_OPERATING_TEMP = 105f;
        private const float PROTECTION_DANGER_TEMP = 118f;
        private const float PROTECTION_MAX_ACCEL_MS2 = 0.25f;
        private const float PROTECTION_BRAKING_TIME = 10f;
        private const float PROTECTION_BRAKE_RELEASE_FACTOR = 0.5f;
        private const float PROTECTION_MIN_BRAKE = 0.1f;
        private const float PROTECTION_BRAKE_SPEED_BAND = 2.5f;
        private const float PROTECTION_AMP_RELEASE_FACTOR = 0.92f;
        private const float DE6_THROTTLE_RISE_RATE = 0.25f;
        private const float DE6_THROTTLE_FALL_RATE = 0.45f;
        private const float DE6_BRAKE_APPLY_RATE = 0.18f;
        private const float DE6_BRAKE_RELEASE_RATE = 0.10f;
        private const float DE6_BRAKE_UNDER_TARGET_RELEASE_RATE = 0.55f;
        private const float DE6_BRAKE_RELEASE_BAND = 1.0f;
        private const float STEAM_BRAKE_APPLY_RATE = 0.22f;
        private const float STEAM_BRAKE_RELEASE_RATE = 0.35f;
        private const float NON_SELF_LAPPING_BRAKE_APPLY_RATE = 0.18f;
        private const float NON_SELF_LAPPING_BRAKE_RELEASE_RATE = 0.30f;
        private const float DE2_MAX_AMPS = 750f;
        private const float DE6_MAX_AMPS = 1450f;
        private const float DM3_MIN_TORQUE = 35000f;

        private float _lastGearRpm    = 0f;
        private float _lastShiftTime  = -999f;
        private bool  _awaitingShift  = false; // throttle zeroed, waiting for RPM to drop before moving lever
        private int   _pendingShiftDir = 0;    // +1 up, -1 down

        // Cached components (lazy-initialised per loco session)
        private LocoIndicatorReader        _indicators;
        private InteriorControlsManager    _interiorControls;
        private bool                       _isDM3;
        private string                     _locoId = "";

        // ── Steam (S060 / S282) state ────────────────────────────────────────
        private bool                       _isSteam;
        private BaseControlsOverrider      _steamOverrider;
        private const float STEAM_CUTOFF_FORWARD_MIN = 0.52f;
        private const float STEAM_CUTOFF_REVERSE_MAX = 0.48f;

        // Smoothed control positions
        private float _steamRegulator      = 0f;
        private float _steamCutoff         = 0.5f;
        private float _steamBrakeTarget    = 0f;
        private float _steamLockedCutoffDirection = 0f;

        // Averaged steam-chest pressure (sampled every 0.1 s, window = 10)
        private readonly Queue<float> _steamPressureSamples = new Queue<float>();
        private float _steamPressureTimer = 0f;

        // Reflection cache for simFlow.TryGetPort
        private SimController _steamSimCtrl;
        private MethodInfo    _tryGetPortMI;
        private PropertyInfo  _portValueProp;
        private bool          _steamEngineScanAttempted;
        private bool          _steamChestMemberResolved;
        private object        _steamEngineComponent;
        private PropertyInfo  _steamChestPressureProperty;
        private FieldInfo     _steamChestPressureField;
        private MethodInfo    _steamChestPressureMethod;
        private FieldInfo     _steamSimComponentsField;
        private bool          _steamRecoveringToTarget;

        // DM3 cache for simFlow.TryGetPort
        private SimController _dm3SimCtrl;
        private MethodInfo _dm3TryGetPortMI;
        private PropertyInfo _dm3PortValueProp;

        // Generic simFlow port cache for DriverAssist-style protection reads
        private SimController _protectionSimCtrl;
        private MethodInfo _protectionTryGetPortMI;
        private PropertyInfo _protectionPortValueProp;

        // DriverAssist-style protection state
        private float _lastProtectionTemp = -1f;
        private float _lastProtectionTempTime = -1f;
        private float _protectionTempRate = 0f;
        private bool _ampProtectionActive = false;
        private bool _de6BrakeProtectionActive = false;
        private readonly AiPulseBrakeController _steamBrakeController = new AiPulseBrakeController();
        private readonly AiPulseBrakeController _nonSelfLappingBrakeController = new AiPulseBrakeController();

        // Brake cylinder reader, adapted from SteamCruiseControl's sensor wrapper.
        private bool _brakeAccessResolved;
        private Component _brakeSystemComponent;
        private FieldInfo _brakeCylinderField;
        private PropertyInfo _brakePressureProperty;
        private bool _brakeUsesInverseLerp;

        // ────────────────────────────────────────────────────────────────────

        public LocoCruiseControl(ILocomotiveRemoteControl remoteControl, TrainCar car = null)
        {
            this.remoteControl = remoteControl;
            this.trainCar      = car;

            if (car != null)
            {
                _locoId       = car.carLivery?.parentType?.id ?? "";
                _isDM3        = _locoId == LOCO_DM3;
                _indicators   = car.GetComponentInChildren<LocoIndicatorReader>();
                // Interior controls loaded lazily (may be null until player enters cab)

                _isSteam = _locoId.Contains("S060") || _locoId.Contains("S282");
                if (_isSteam)
                    _steamOverrider = car.SimController?.controlsOverrider;
            }
        }

        public bool StartCruiseControl(float targetSpeed)
        {
            _steamLockedCutoffDirection = 0f;
            _steamBrakeController.Clear();
            _nonSelfLappingBrakeController.Clear();
            this.TargetSpeed = targetSpeed;
            running = true;
            Module.StartCoroutine(CruiseControlCoroutine());
            return true;
        }

        private const float Kp_SPEED             = 1.5f;
        private const float Ki_SPEED             = 0.0005f;
        private const float Kd_SPEED             = 7f;
        private const float Kp_SPEED_RETURN_FACTOR = 1.5f;

        float integral      = 0;
        float previousError = 0;

        // ── Main speed controller ────────────────────────────────────────────
        //https://en.wikipedia.org/wiki/PID_controller
        protected float MaintainSpeed(float targetAcceleration, float dt, float speed, float acceleration)
        {
            // Steam locos use direct regulator/cutoff/brake control, not the PID
            if (_isSteam)
                return MaintainSpeedSteam(dt, speed);

            if (remoteControl.GetReverserSymbol() == "N")
            {
                running = false;
                return 0.0f;
            }

            // ── DM3: cap speed and handle gear shift ─────────────────────────
            if (_isDM3)
            {
                if (TargetSpeed > DM3_MAX_SPEED)
                    TargetSpeed = DM3_MAX_SPEED;
                if (HandleDM3GearShift(dt))
                    return targetAcceleration; // PID skipped during shift
            }

            // ── Temperature: back off throttle when overheating ──────────────
            var tempState = remoteControl.GetEngineTemperatureState(false);
            bool tempCritical = tempState.HasFlag(MultipleUnitStateObserver.TemperatureState.Critical);
            bool tempWarning  = tempState.HasFlag(MultipleUnitStateObserver.TemperatureState.Warning);

            // ── PID ──────────────────────────────────────────────────────────
            float error = TargetSpeed - speed;
            integral += error * dt;

            if (error < 0)
                integral = 0;

            float derivative = (error - previousError) / dt;

            float Kp = error < 0 ? Kp_SPEED * Kp_SPEED_RETURN_FACTOR : Kp_SPEED;

            float controlValue = Kp * error + Ki_SPEED * integral + Kd_SPEED * derivative;

            previousError = error;

            controlValue = Mathf.Clamp(controlValue, -20f, 20f);

            if (acceleration > targetAcceleration)
                controlValue = 0f;

            if (TargetSpeed < Mathf.Epsilon)
                controlValue = -20f;

            // Temperature limiting: Critical → force reduce; Warning → no increase
            if (tempCritical)
                controlValue = Mathf.Min(controlValue, -5f);
            else if (tempWarning)
                controlValue = Mathf.Min(controlValue, 0f);

            if (remoteControl.IsWheelslipping())
            {
                targetAcceleration -= 0.5f * dt;
                controlValue = -20.0f;
            }

#if DEBUG2
            Terminal.Log($"targetSpeed {TargetSpeed} error {error} accel {acceleration} controlValue {controlValue} temp {tempState}");
#endif

            // ── Throttle: set directly to bypass UpdateThrottle's throttleStepSize scaling ──
            // UpdateThrottle(factor) internally does: throttle += factor * throttleStepSize (~0.125)
            // which makes factors from the PID near-zero above 60% throttle.
            // Direct Set() on controlsOverrider applies the full delta immediately.
            var simOverrider = trainCar?.SimController?.controlsOverrider;
            if (simOverrider?.Throttle != null)
            {
                // controlValue in [-20..20]; rate = 0.025 → max 0.5 throttle/s at full command
                float newThrottle = Mathf.Clamp01(simOverrider.Throttle.Value + controlValue * 0.025f * dt);
                // Cap throttle at low speed to prevent traction motor overload (DE4 etc.)
                if (speed < 5f)  newThrottle = Mathf.Min(newThrottle, 0.25f);
                else if (speed < 15f) newThrottle = Mathf.Min(newThrottle, 0.55f);
                newThrottle = ApplyThrottleProtection(newThrottle, simOverrider.Throttle.Value, speed, acceleration, dt);
                simOverrider.Throttle.Set(newThrottle);
            }
            else
            {
                remoteControl.UpdateThrottle(controlValue * 2.0f * dt);
            }

            // ── Brake: proportional to overspeed ─────────────────────────────
            if (simOverrider?.Brake != null)
                ApplyPredictiveBrake(simOverrider, speed, acceleration, error, dt);
            else if (error < -3.0f || TargetSpeed < Mathf.Epsilon)
                remoteControl.UpdateBrake(0.3f * error * -1.0f * dt);
            else if (remoteControl.GetTargetBrake() > Mathf.Epsilon)
                remoteControl.UpdateBrake(-30.0f * dt);

            return targetAcceleration;
        }

        private float ApplyThrottleProtection(float requestedThrottle, float currentThrottle, float speedKmh, float accelerationKmhS, float dt)
        {
            float cappedThrottle = requestedThrottle;
            float temp = GetLocoTemperature();
            float projectedTemp = UpdateProjectedTemperature(temp);
            float maxAmps = GetMaxSafeAmps();
            float amps = GetLocoAmps();
            float accelerationMs2 = accelerationKmhS / 3.6f;

            bool reduceThrottle = false;

            if (projectedTemp >= PROTECTION_DANGER_TEMP)
            {
                reduceThrottle = true;
            }
            else if (projectedTemp >= PROTECTION_OPERATING_TEMP && _protectionTempRate >= 0f && accelerationMs2 > 0.025f)
            {
                reduceThrottle = true;
            }

            if (maxAmps > 0f)
            {
                if (_ampProtectionActive)
                    _ampProtectionActive = amps > maxAmps * PROTECTION_AMP_RELEASE_FACTOR;
                else
                    _ampProtectionActive = amps >= maxAmps;

                if (_ampProtectionActive)
                    reduceThrottle = true;
            }

            if (remoteControl.IsWheelslipping())
                reduceThrottle = true;

            if (accelerationMs2 >= PROTECTION_MAX_ACCEL_MS2 && currentThrottle > THROTTLE_NOTCH)
                reduceThrottle = true;

            if (reduceThrottle)
                cappedThrottle = Mathf.Min(cappedThrottle, Mathf.Max(0f, currentThrottle - THROTTLE_NOTCH));
            else if (_isDM3 && ShouldAddDm3HillClimbThrottle(cappedThrottle, currentThrottle, speedKmh, accelerationMs2, projectedTemp))
                cappedThrottle = Mathf.Max(cappedThrottle, Mathf.Min(1f, currentThrottle + THROTTLE_NOTCH));

            if (IsDE6Like())
            {
                float maxRise = DE6_THROTTLE_RISE_RATE * dt;
                float maxFall = DE6_THROTTLE_FALL_RATE * dt;
                cappedThrottle = Mathf.Clamp(cappedThrottle, currentThrottle - maxFall, currentThrottle + maxRise);
            }

            return Mathf.Clamp01(cappedThrottle);
        }

        private bool IsDE6Like()
        {
            string id = _locoId ?? "";
            return id.Contains("DE6");
        }

        private bool ShouldAddDm3HillClimbThrottle(float requestedThrottle, float currentThrottle, float speedKmh, float accelerationMs2, float projectedTemp)
        {
            if (requestedThrottle <= currentThrottle || currentThrottle >= 1f)
                return false;
            if (projectedTemp >= PROTECTION_OPERATING_TEMP)
                return false;
            if (accelerationMs2 >= PROTECTION_MAX_ACCEL_MS2)
                return false;

            float torque = Mathf.Abs(GetSimPortValue("traction.TORQUE_IN"));
            if (torque <= 0f)
                return false;

            return torque < DM3_MIN_TORQUE && accelerationMs2 < 0.05f && speedKmh < TargetSpeed - 1f;
        }

        private void ApplyPredictiveBrake(BaseControlsOverrider simOverrider, float speedKmh, float accelerationKmhS, float speedError, float dt)
        {
            bool lightEngine = trainCar?.trainset?.cars != null && trainCar.trainset.cars.Count == 1;
            float trainBrake = simOverrider.Brake?.Value ?? 0f;
            float independentBrake = simOverrider.IndependentBrake?.Value ?? 0f;
            float activeBrake = lightEngine ? independentBrake : trainBrake;
            float brakeTarget = 0f;

            if (TargetSpeed < Mathf.Epsilon)
            {
                _nonSelfLappingBrakeController.Clear();
                brakeTarget = 1f;
            }
            else if (!lightEngine && IsNonSelfLappingTrainBrake(simOverrider))
            {
                float projectedSpeed = speedKmh + accelerationKmhS * PROTECTION_BRAKING_TIME;
                bool projectedOverspeed = speedKmh > TargetSpeed - 1f && projectedSpeed > TargetSpeed + PROTECTION_BRAKE_SPEED_BAND;
                bool actualOverspeed = speedError < -3f;

                if (projectedOverspeed || actualOverspeed)
                {
                    float overspeed = Mathf.Max(0f, Mathf.Max(projectedSpeed - TargetSpeed, -speedError - 3f));
                    BrakeHeatState heat = GetBrakeHeatState();
                    brakeTarget = _nonSelfLappingBrakeController.Update(dt, overspeed, heat.OverheatPercentage, true, GetBrakeCylinderPressure(), heat.TemperatureC);
                }
                else
                {
                    _nonSelfLappingBrakeController.Clear();
                    brakeTarget = Mathf.Max(0f, activeBrake - PROTECTION_BRAKE_RELEASE_FACTOR * activeBrake);
                }

                float maxApply = NON_SELF_LAPPING_BRAKE_APPLY_RATE * dt;
                float maxRelease = NON_SELF_LAPPING_BRAKE_RELEASE_RATE * dt;
                brakeTarget = Mathf.Clamp(brakeTarget, activeBrake - maxRelease, activeBrake + maxApply);
            }
            else
            {
                _nonSelfLappingBrakeController.Clear();
                float projectedSpeed = speedKmh + accelerationKmhS * PROTECTION_BRAKING_TIME;
                bool projectedOverspeed = speedKmh > TargetSpeed - 1f && projectedSpeed > TargetSpeed + PROTECTION_BRAKE_SPEED_BAND;
                bool actualOverspeed = speedError < -3f;

                if (IsDE6Like())
                {
                    if (_de6BrakeProtectionActive)
                        _de6BrakeProtectionActive = speedError < -DE6_BRAKE_RELEASE_BAND || projectedSpeed > TargetSpeed + DE6_BRAKE_RELEASE_BAND;
                    else
                        _de6BrakeProtectionActive = projectedOverspeed || actualOverspeed;

                    projectedOverspeed = _de6BrakeProtectionActive && projectedSpeed > TargetSpeed + DE6_BRAKE_RELEASE_BAND;
                    actualOverspeed = _de6BrakeProtectionActive && speedError < -DE6_BRAKE_RELEASE_BAND;
                }

                if (projectedOverspeed)
                    brakeTarget = Mathf.Clamp01(activeBrake + THROTTLE_NOTCH);
                else if (actualOverspeed)
                    brakeTarget = Mathf.Clamp01(Mathf.Max(PROTECTION_MIN_BRAKE, (-speedError - 3f) / 20f));
                else
                    brakeTarget = Mathf.Max(0f, activeBrake - PROTECTION_BRAKE_RELEASE_FACTOR * activeBrake);

                if (IsDE6Like())
                {
                    float maxApply = DE6_BRAKE_APPLY_RATE * dt;
                    float releaseRate = speedError > 1f ? DE6_BRAKE_UNDER_TARGET_RELEASE_RATE : DE6_BRAKE_RELEASE_RATE;
                    float maxRelease = releaseRate * dt;
                    brakeTarget = Mathf.Clamp(brakeTarget, activeBrake - maxRelease, activeBrake + maxApply);
                }
            }

            if (lightEngine)
            {
                simOverrider.Brake.Set(0f);
                simOverrider.IndependentBrake?.Set(brakeTarget);
            }
            else
            {
                simOverrider.Brake.Set(brakeTarget);
                simOverrider.IndependentBrake?.Set(0f);
            }
        }

        private bool IsNonSelfLappingTrainBrake(BaseControlsOverrider simOverrider)
        {
            if (_isDM3)
                return true;

            var brake = simOverrider?.Brake;
            if (brake == null || !brake.IsNotched)
                return false;

            return brake.NotchCount > 0 && brake.NotchCount <= 4;
        }

        private BrakeHeatState GetBrakeHeatState()
        {
            float maxTemp = 25f;
            float maxOverheat = 0f;

            try
            {
                var cars = trainCar?.trainset?.cars;
                if (cars != null)
                {
                    foreach (TrainCar car in cars)
                    {
                        var heat = car?.brakeSystem?.heatController;
                        if (heat == null)
                            continue;

                        maxTemp = Mathf.Max(maxTemp, heat.temperature);
                        maxOverheat = Mathf.Max(maxOverheat, heat.overheatPercentage);
                    }
                }
            }
            catch
            {
                return new BrakeHeatState(maxTemp, maxOverheat);
            }

            return new BrakeHeatState(maxTemp, maxOverheat);
        }

        private float GetBrakeCylinderPressure()
        {
            try
            {
                if (!_brakeAccessResolved)
                    ResolveBrakeAccess();

                if (_brakeSystemComponent == null)
                    return 0f;

                if (_brakeCylinderField != null && _brakeCylinderField.GetValue(_brakeSystemComponent) is float cylinder)
                    return _brakeUsesInverseLerp ? Mathf.InverseLerp(1f, 4.5f, cylinder) : cylinder;
                if (_brakePressureProperty != null && _brakePressureProperty.GetValue(_brakeSystemComponent) is float pressure)
                    return pressure;
            }
            catch { }

            return 0f;
        }

        private void ResolveBrakeAccess()
        {
            _brakeAccessResolved = true;
            try
            {
                if (trainCar == null)
                    return;

                Component[] components = trainCar.GetComponentsInChildren<Component>(true);
                foreach (Component component in components)
                {
                    if (component == null || component.GetType().Name.IndexOf("BrakeSystem", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    FieldInfo cylinderField = component.GetType().GetField("brakeCylinderPressure", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cylinderField != null)
                    {
                        _brakeSystemComponent = component;
                        _brakeCylinderField = cylinderField;
                        _brakeUsesInverseLerp = true;
                        return;
                    }

                    PropertyInfo pressureProperty = component.GetType().GetProperty("brakePressure", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pressureProperty != null)
                    {
                        _brakeSystemComponent = component;
                        _brakePressureProperty = pressureProperty;
                        _brakeUsesInverseLerp = false;
                        return;
                    }
                }
            }
            catch { }
        }

        private struct BrakeHeatState
        {
            public readonly float TemperatureC;
            public readonly float OverheatPercentage;

            public BrakeHeatState(float temperatureC, float overheatPercentage)
            {
                TemperatureC = temperatureC;
                OverheatPercentage = overheatPercentage;
            }
        }

        private sealed class AiPulseBrakeController
        {
            private const float UNDERSHOOT_GUARD_KMH = 0.25f;
            private const float OVERSPEED_BAND_KMH = 2f;
            private const float OVERSPEED_FULL_RANGE_KMH = 18f;
            private const float PULSE_HIGH_BASE_SEC = 4f;
            private const float PULSE_LOW_BASE_SEC = 3f;
            private const float PULSE_LOW_TIMEOUT_MULTIPLIER = 3f;
            private const float MIN_BRAKE_AT_LOW_OVERSPEED = 0.55f;
            private const float PRE_OVERHEAT_START_C = 450f;
            private const float OVERHEAT_ONSET_C = 600f;

            private bool _active;
            private bool _inPulseHigh;
            private float _timer;

            public float Update(float deltaTime, float overspeedKmh, float overheatingPct, bool waitForCylinderRelease, float brakeCylinderPressure, float brakeTempC)
            {
                if (overspeedKmh <= UNDERSHOOT_GUARD_KMH)
                {
                    Clear();
                    return 0f;
                }

                if (!_active)
                {
                    _active = true;
                    _inPulseHigh = true;
                    _timer = 0f;
                }

                float heatFade = Mathf.Clamp01((brakeTempC - PRE_OVERHEAT_START_C) / (OVERHEAT_ONSET_C - PRE_OVERHEAT_START_C));
                float highDuration = Mathf.Max(2f, PULSE_HIGH_BASE_SEC * (1f - overheatingPct * 0.5f) * (1f - heatFade * 0.25f));
                float lowDuration = Mathf.Max(2f, PULSE_LOW_BASE_SEC * (1f + overheatingPct * 2f) * (1f + heatFade));
                float lowTimeout = lowDuration * PULSE_LOW_TIMEOUT_MULTIPLIER;

                _timer += deltaTime;
                if (_inPulseHigh)
                {
                    if (_timer >= highDuration)
                    {
                        _inPulseHigh = false;
                        _timer = 0f;
                    }
                }
                else
                {
                    bool cylinderReleased = !waitForCylinderRelease || brakeCylinderPressure <= 0.1f;
                    if ((_timer >= lowDuration && cylinderReleased) || _timer >= lowTimeout)
                    {
                        _inPulseHigh = true;
                        _timer = 0f;
                    }
                }

                if (!_inPulseHigh)
                    return 0f;

                float heatForceScale = 1f;
                if (overheatingPct > 0.4f)
                    heatForceScale = overheatingPct > 0.7f ? 0.4f : 1f - (overheatingPct - 0.4f) / 0.3f * 0.6f;

                float overspeedFactor = Mathf.Clamp01((overspeedKmh - OVERSPEED_BAND_KMH) / OVERSPEED_FULL_RANGE_KMH);
                return (MIN_BRAKE_AT_LOW_OVERSPEED + (1f - MIN_BRAKE_AT_LOW_OVERSPEED) * overspeedFactor) * heatForceScale;
            }

            public void Clear()
            {
                _active = false;
                _inPulseHigh = false;
                _timer = 0f;
            }
        }

        private float GetLocoTemperature()
        {
            try
            {
                if (_indicators == null)
                    _indicators = trainCar?.GetComponentInChildren<LocoIndicatorReader>();

                if (_indicators?.tmTemp != null)
                    return _indicators.tmTemp.Value;
                if (_indicators?.oilTemp != null)
                    return _indicators.oilTemp.Value;
            }
            catch { }

            return 0f;
        }

        private float UpdateProjectedTemperature(float currentTemp)
        {
            if (currentTemp <= 0f)
                return 0f;

            float now = Time.time;
            if (_lastProtectionTemp > 0f && _lastProtectionTempTime >= 0f)
            {
                float dt = Mathf.Max(0.1f, now - _lastProtectionTempTime);
                _protectionTempRate = (currentTemp - _lastProtectionTemp) / dt;
            }

            _lastProtectionTemp = currentTemp;
            _lastProtectionTempTime = now;
            return currentTemp + _protectionTempRate;
        }

        private float GetLocoAmps()
        {
            try
            {
                if (_indicators == null)
                    _indicators = trainCar?.GetComponentInChildren<LocoIndicatorReader>();

                return _indicators?.amps != null ? _indicators.amps.Value : 0f;
            }
            catch { return 0f; }
        }

        private float GetMaxSafeAmps()
        {
            string id = _locoId ?? "";
            if (id.Contains("DE2") || id.Contains("Shunter"))
                return DE2_MAX_AMPS;
            if (id.Contains("DE6") || id.Contains("Diesel"))
                return DE6_MAX_AMPS;
            return 0f;
        }

        private float GetSimPortValue(string portId)
        {
            try
            {
                if (_protectionSimCtrl == null)
                    _protectionSimCtrl = trainCar?.SimController;

                if (_protectionSimCtrl?.simFlow == null)
                    return 0f;

                if (_protectionTryGetPortMI == null)
                    _protectionTryGetPortMI = _protectionSimCtrl.simFlow.GetType()
                        .GetMethod("TryGetPort", BindingFlags.Instance | BindingFlags.Public);

                if (_protectionTryGetPortMI == null)
                    return 0f;

                var args = new object[] { portId, null, true };
                if (!(bool)_protectionTryGetPortMI.Invoke(_protectionSimCtrl.simFlow, args) || args[1] == null)
                    return 0f;

                if (_protectionPortValueProp == null)
                    _protectionPortValueProp = args[1].GetType().GetProperty("Value");

                return _protectionPortValueProp != null
                    ? (float)_protectionPortValueProp.GetValue(args[1])
                    : 0f;
            }
            catch { return 0f; }
        }

        private float GetDM3Rpm()
        {
            try
            {
                if (_dm3SimCtrl == null)
                    _dm3SimCtrl = trainCar?.SimController;

                if (_dm3SimCtrl?.simFlow == null)
                    return -1f;

                if (_dm3TryGetPortMI == null)
                    _dm3TryGetPortMI = _dm3SimCtrl.simFlow.GetType()
                        .GetMethod("TryGetPort", BindingFlags.Instance | BindingFlags.Public);

                if (_dm3TryGetPortMI == null)
                    return -1f;

                var args = new object[] { "de.RPM", null, true };
                if (!(bool)_dm3TryGetPortMI.Invoke(_dm3SimCtrl.simFlow, args) || args[1] == null)
                    return -1f;

                if (_dm3PortValueProp == null)
                    _dm3PortValueProp = args[1].GetType().GetProperty("Value");

                return _dm3PortValueProp != null
                    ? (float)_dm3PortValueProp.GetValue(args[1])
                    : -1f;
            }
            catch
            {
                return -1f;
            }
        }

        private struct DM3GearPosition
        {
            public int GearA;
            public int GearB;

            public DM3GearPosition(int gearA, int gearB)
            {
                GearA = gearA;
                GearB = gearB;
            }

            public override string ToString()
            {
                return $"{GearA},{GearB}";
            }
        }

        private int _currentDM3GearIndex = 0;

        private static readonly DM3GearPosition[] DM3_GEAR_SEQUENCE =
        {
            new DM3GearPosition(1, 1),
            new DM3GearPosition(1, 2),
            new DM3GearPosition(2, 1),
            new DM3GearPosition(2, 2),
            new DM3GearPosition(3, 1),
            // new DM3GearPosition(1, 3), // intentionally skipped
            new DM3GearPosition(3, 2),
            new DM3GearPosition(2, 3),
            new DM3GearPosition(3, 3),
        };

        private bool ShiftDM3Gear(int direction)
        {
            var controls = GetInteriorControls();
            if (controls == null)
                return false;

            int targetIndex = _currentDM3GearIndex + direction;

            DM3GearPosition current = DM3_GEAR_SEQUENCE[_currentDM3GearIndex];
            DM3GearPosition target = DM3_GEAR_SEQUENCE[targetIndex];

            if (target.GearA > current.GearA)
                controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxA, 1);
            else if (target.GearA < current.GearA)
                controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxA, -1);

            if (target.GearB > current.GearB)
                controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxB, 1);
            else if (target.GearB < current.GearB)
                controls.MoveScrollable(InteriorControlsManager.ControlType.GearboxB, -1);

            _currentDM3GearIndex = targetIndex;
            return true;
        }

        private static int? ParseDM3GearPosition(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            foreach (char c in value)
            {
                if (c >= '1' && c <= '3')
                    return c - '0';
            }

            return null;
        }

        private void SyncDM3GearIndexFromControls()
        {
            var controls = GetInteriorControls();
            if (controls == null)
                return;

            var gearA = ParseDM3GearPosition(controls.GetCurrentPositionName(InteriorControlsManager.ControlType.GearboxA).value);
            var gearB = ParseDM3GearPosition(controls.GetCurrentPositionName(InteriorControlsManager.ControlType.GearboxB).value);
            if (!gearA.HasValue || !gearB.HasValue)
                return;

            for (int i = 0; i < DM3_GEAR_SEQUENCE.Length; i++)
            {
                if (DM3_GEAR_SEQUENCE[i].GearA == gearA.Value && DM3_GEAR_SEQUENCE[i].GearB == gearB.Value)
                {
                    _currentDM3GearIndex = i;
                    return;
                }
            }
        }

        // ── DM3 gear management ──────────────────────────────────────────────
        // Returns true while a shift is in progress (PID caller should skip output).
        private bool HandleDM3GearShift(float dt)
        {
            float rpm = GetDM3Rpm();
            if (rpm < 0f)
                return false;

            float now = Time.time;
            if (!_awaitingShift)
                SyncDM3GearIndexFromControls();

            if (_awaitingShift)
            {
                int targetIndex = _currentDM3GearIndex + _pendingShiftDir;
                if (targetIndex < 0 || targetIndex >= DM3_GEAR_SEQUENCE.Length)
                {
                    _lastGearRpm = rpm;
                    _awaitingShift = false;
                    return false; // Dont shift if already on min or max gear
                }

                // Zero throttle and wait for RPM to settle before moving the lever
                remoteControl.UpdateThrottle(-100f);

                if (rpm < 750f)
                {
                    // RPM low enough — move the gear lever
                    var controls = GetInteriorControls();
                    if (controls != null)
                    {
                        ShiftDM3Gear(_pendingShiftDir);
#if DEBUG
                        Terminal.Log($"DM3: shifted to {DM3_GEAR_SEQUENCE[_currentDM3GearIndex]} (RPM {rpm:0})");
#endif
                        _awaitingShift = false;
                        _lastShiftTime = now;
                    }
                }

                _lastGearRpm = rpm;
                return true; // PID suppressed
            }

            // Cooldown between shifts
            if (now - _lastShiftTime < SHIFT_COOLDOWN)
            {
                _lastGearRpm = rpm;
                return false;
            }

            // Decide whether a shift is needed
            if (rpm > RPM_SHIFT_UP)
            {
                _awaitingShift  = true;
                _pendingShiftDir = 1;
#if DEBUG
                Terminal.Log($"DM3: shift-up queued (RPM {rpm:0})");
#endif
            }
            else if (rpm < RPM_SHIFT_DOWN && rpm <= _lastGearRpm)
            {
                _awaitingShift  = true;
                _pendingShiftDir = -1;
#if DEBUG
                Terminal.Log($"DM3: shift-down queued (RPM {rpm:0})");
#endif
            }

            _lastGearRpm = rpm;
            return _awaitingShift;
        }

        private InteriorControlsManager GetInteriorControls()
        {
            if (_interiorControls != null) return _interiorControls;
            if (trainCar?.interior == null) return null;
            _interiorControls = trainCar.interior.GetComponentInChildren<InteriorControlsManager>(true);
            return _interiorControls;
        }

        // ── Steam loco controller ────────────────────────────────────────────
        // Mirrors the algorithm from SteamCruiseControl mod's CruiseControlAI:
        //   - Direct absolute Set() on regulator, cutoff, brake (no PID)
        //   - Pressure-aware cutoff: reduce steam admission as chest pressure rises
        //   - Smooth capped service braking when overspeeding
        private float MaintainSpeedSteam(float dt, float speed)
        {
            if (_steamOverrider == null) return 0f;

            float signedTarget = GetSteamSignedTargetSpeed();
            bool forward  = signedTarget >= 0f;
            float absTarget = Mathf.Abs(signedTarget);
            float absSpeed  = Mathf.Abs(speed);

            float pressure    = GetSteamChestPressure();
            float avgPressure = UpdateSteamPressureAvg(pressure, dt);

            // ── Full stop ────────────────────────────────────────────────────
            if (absTarget < Mathf.Epsilon)
            {
                _steamBrakeController.Clear();
                SteamSetBrake(1f);
                SteamSetRegulatorSmooth(0f);
                SteamSetCutoffSmooth(0.5f, dt);
                return 0f;
            }

            float speedDiff = absSpeed - absTarget; // positive → too fast

            if (speedDiff > 2.0f)
            {
                _steamRecoveringToTarget = false;

                BrakeHeatState heat = GetBrakeHeatState();
                float brakeVal = _steamBrakeController.Update(dt, speedDiff, heat.OverheatPercentage, true, GetBrakeCylinderPressure(), heat.TemperatureC);
                SteamSetBrakeSmooth(brakeVal, dt);
                SteamSetRegulatorSmooth(0f);
                // Cutoff to neutral while braking — avoids steam fighting brakes
                SteamSetCutoffSmooth(forward ? 0.5f : 0.49f, dt);
            }
            else
            {
                // ── Not braking ───────────────────────────────────────────────
                _steamBrakeController.Clear();
                SteamSetBrakeSmooth(0f, dt);

                if (absSpeed < absTarget - 2.0f)
                    _steamRecoveringToTarget = true;

                if (_steamRecoveringToTarget && absSpeed >= absTarget - 0.5f)
                    _steamRecoveringToTarget = false;

                if (_steamRecoveringToTarget)
                {
                    // ── Accelerating ──────────────────────────────────────────
                    float targetRegulator, targetCutoff;

                    if (avgPressure < 0f)
                        avgPressure = 8f;

                    const float PRESSURE_TARGET = 12f;
                    const float REGULATOR_RAMP_END_SPEED = 20f;

                    if (absSpeed < REGULATOR_RAMP_END_SPEED)
                    {
                        // SteamCruiseControl ramps regulator in gently while keeping full cutoff.
                        float ramp = Mathf.Clamp01(absSpeed / REGULATOR_RAMP_END_SPEED);
                        targetRegulator = 0.1f + 0.9f * ramp;
                        targetCutoff    = forward ? 1f : 0f;
                    }
                    else
                    {
                        targetRegulator = 1f;
                        float pressureError = avgPressure - (PRESSURE_TARGET - 1f);
                        float normalized = Mathf.Clamp(pressureError / 3f, -1f, 1f);
                        float shaped = Mathf.Sign(normalized) * Mathf.Pow(Mathf.Abs(normalized), 0.7f);
                        float pressureCutoff = forward
                            ? Mathf.Lerp(0.55f, 0.825f, (shaped + 1f) * 0.5f)
                            : Mathf.Lerp(0.48f, 0.175f, (shaped + 1f) * 0.5f);

                        // Blend out of full cutoff between 20 and 40 km/h like SteamCruiseControl.
                        float blendEndSpeed = REGULATOR_RAMP_END_SPEED * 2f;
                        if ((absTarget > 0.1f && absSpeed < absTarget - 2f) || absSpeed >= blendEndSpeed)
                        {
                            targetCutoff = pressureCutoff;
                        }
                        else
                        {
                            float blend = Mathf.Clamp01((absSpeed - REGULATOR_RAMP_END_SPEED) / REGULATOR_RAMP_END_SPEED);
                            targetCutoff = Mathf.Lerp(forward ? 1f : 0f, pressureCutoff, blend);
                        }
                    }

                    SteamSetRegulatorSmooth(targetRegulator);
                    SteamSetCutoffSmooth(targetCutoff, dt);
                }
                else
                {
                    // ── Coasting at target speed ──────────────────────────────
                    SteamSetRegulatorSmooth(0f);
                    SteamSetCutoffSmooth(0.5f, dt);
                }
            }

            return 0f;
        }

        private bool IsSteamReverserForward()
        {
            if (Mathf.Abs(_steamLockedCutoffDirection) > 0.01f)
                return _steamLockedCutoffDirection > 0f;

            string reverser = remoteControl?.GetReverserSymbol();
            if (reverser == "R")
                return false;
            if (reverser == "F")
                return true;
            return _steamCutoff >= 0.5f;
        }

        private float GetSteamSignedTargetSpeed()
        {
            if (Mathf.Abs(TargetSpeed) < Mathf.Epsilon)
                return 0f;
            if (Mathf.Abs(_steamLockedCutoffDirection) > 0.01f)
                return Mathf.Abs(TargetSpeed) * Mathf.Sign(_steamLockedCutoffDirection);
            return TargetSpeed;
        }

        protected void SnapSteamCutoffForDirection(bool forward)
        {
            if (!_isSteam || _steamOverrider?.Reverser == null)
                return;

            _steamRecoveringToTarget = true;
            _steamLockedCutoffDirection = forward ? 1f : -1f;
            _steamCutoff = forward ? STEAM_CUTOFF_FORWARD_MIN : STEAM_CUTOFF_REVERSE_MAX;
            _steamOverrider.Reverser.Set(_steamCutoff);
        }

        private void SteamSetRegulatorSmooth(float target)
        {
            _steamRegulator = Mathf.Lerp(_steamRegulator, target, 0.15f);
            _steamOverrider?.Throttle?.Set(_steamRegulator);
        }

        // cutoff 0 = full reverse, 0.5 = neutral, 1 = full forward
        private void SteamSetCutoffSmooth(float target, float dt)
        {
            if (Mathf.Abs(_steamLockedCutoffDirection) > 0.01f)
            {
                target = _steamLockedCutoffDirection > 0f
                    ? Mathf.Max(STEAM_CUTOFF_FORWARD_MIN, target)
                    : Mathf.Min(STEAM_CUTOFF_REVERSE_MAX, target);
            }

            float delta = target - _steamCutoff;
            if (Mathf.Abs(delta) > 0.005f)
                _steamCutoff += delta * 0.15f * dt;
            else
                _steamCutoff = target;

            _steamOverrider?.Reverser?.Set(_steamCutoff);
        }

        private void SteamSetBrake(float value)
        {
            _steamBrakeTarget = value;
            _steamOverrider?.Brake?.Set(value);
        }

        private void SteamSetBrakeSmooth(float target, float dt)
        {
            target = Mathf.Clamp01(target);
            float rate = target > _steamBrakeTarget ? STEAM_BRAKE_APPLY_RATE : STEAM_BRAKE_RELEASE_RATE;
            _steamBrakeTarget = Mathf.MoveTowards(_steamBrakeTarget, target, rate * dt);
            _steamOverrider?.Brake?.Set(_steamBrakeTarget);
        }

        private float UpdateSteamPressureAvg(float currentPressure, float dt)
        {
            _steamPressureTimer += dt;
            if (_steamPressureTimer >= 0.1f)
            {
                _steamPressureSamples.Enqueue(currentPressure);
                _steamPressureTimer = 0f;
                while (_steamPressureSamples.Count > 10)
                    _steamPressureSamples.Dequeue();
            }

            if (_steamPressureSamples.Count == 0) return currentPressure;

            float sum = 0f;
            foreach (float s in _steamPressureSamples) sum += s;
            return sum / _steamPressureSamples.Count;
        }

        private float GetSteamChestPressure()
        {
            float pressure = TryGetSteamChestPressureFromSimFlow();
            if (!float.IsNaN(pressure))
                return pressure;

            pressure = TryGetSteamChestPressureFromEngine();
            if (!float.IsNaN(pressure))
                return pressure;

            return 8f;
        }

        private float TryGetSteamChestPressureFromSimFlow()
        {
            try
            {
                if (_steamSimCtrl == null)
                    _steamSimCtrl = trainCar?.SimController ?? trainCar?.GetComponentInChildren<SimController>(true);
                if (_steamSimCtrl?.simFlow == null) return float.NaN;

                if (_tryGetPortMI == null)
                    _tryGetPortMI = _steamSimCtrl.simFlow.GetType()
                        .GetMethod("TryGetPort", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_tryGetPortMI == null) return float.NaN;

                var args = new object[] { "steamEngine.STEAM_CHEST_PRESSURE", null, true };
                if (!(bool)_tryGetPortMI.Invoke(_steamSimCtrl.simFlow, args) || args[1] == null)
                    return float.NaN;

                if (_portValueProp == null)
                    _portValueProp = args[1].GetType().GetProperty("Value");

                return _portValueProp != null ? Convert.ToSingle(_portValueProp.GetValue(args[1])) - 1f : float.NaN;
            }
            catch { return float.NaN; }
        }

        private float TryGetSteamChestPressureFromEngine()
        {
            try
            {
                if (_steamEngineComponent == null && !_steamEngineScanAttempted)
                {
                    _steamEngineScanAttempted = true;
                    _steamEngineComponent = FindSteamEngineComponent();
                }

                if (_steamEngineComponent == null)
                    return float.NaN;

                if (!_steamChestMemberResolved)
                {
                    _steamChestMemberResolved = true;
                    ResolveSteamChestPressureMember(_steamEngineComponent);
                }

                if (_steamChestPressureProperty != null)
                    return Convert.ToSingle(_steamChestPressureProperty.GetValue(_steamEngineComponent));
                if (_steamChestPressureField != null)
                    return Convert.ToSingle(_steamChestPressureField.GetValue(_steamEngineComponent));
                if (_steamChestPressureMethod != null)
                    return Convert.ToSingle(_steamChestPressureMethod.Invoke(_steamEngineComponent, null));
            }
            catch { }

            return float.NaN;
        }

        private object FindSteamEngineComponent()
        {
            try
            {
                SimController sim = _steamSimCtrl ?? trainCar?.SimController ?? trainCar?.GetComponentInChildren<SimController>(true);
                if (sim == null)
                    return null;

                if (_steamSimComponentsField == null)
                {
                    Type simType = sim.GetType();
                    _steamSimComponentsField = simType.GetField("simComponents", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? simType.GetField("components", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                var components = _steamSimComponentsField?.GetValue(sim) as IEnumerable;
                if (components == null)
                    return null;

                foreach (object component in components)
                {
                    Type type = component?.GetType();
                    string name = type?.FullName ?? type?.Name ?? "";
                    if (name.IndexOf("ReciprocatingSteamEngine", StringComparison.OrdinalIgnoreCase) >= 0)
                        return component;
                }
            }
            catch { }

            return null;
        }

        private void ResolveSteamChestPressureMember(object engine)
        {
            if (engine == null)
                return;

            Type type = engine.GetType();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (IsSteamChestPressureMember(property.Name) && IsNumericType(property.PropertyType))
                {
                    _steamChestPressureProperty = property;
                    return;
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (IsSteamChestPressureMember(field.Name) && IsNumericType(field.FieldType))
                {
                    _steamChestPressureField = field;
                    return;
                }
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetParameters().Length == 0 && IsSteamChestPressureMember(method.Name) && IsNumericType(method.ReturnType))
                {
                    _steamChestPressureMethod = method;
                    return;
                }
            }
        }

        private static bool IsSteamChestPressureMember(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            string lower = name.ToLowerInvariant();
            return lower.Contains("steam") && lower.Contains("chest");
        }

        private static bool IsNumericType(Type type)
        {
            if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(long) || type == typeof(decimal))
                return true;
            return type != null && !type.IsEnum && typeof(IConvertible).IsAssignableFrom(type);
        }

        // ────────────────────────────────────────────────────────────────────

        public void Stop()
        {
            running = false;
            TargetSpeed = 0f;
            _steamBrakeController.Clear();
            _nonSelfLappingBrakeController.Clear();
            var simOverrider = trainCar?.SimController?.controlsOverrider;
            simOverrider?.Throttle?.Set(0f);

            if (_isSteam && _steamOverrider != null)
            {
                _steamRegulator = 0f;
                _steamRecoveringToTarget = false;
                _steamLockedCutoffDirection = 0f;
                _steamOverrider.Throttle?.Set(0f);
                _steamOverrider.Brake?.Set(1f);
                _steamOverrider.Reverser?.Set(0.5f);
            }
        }

        protected IEnumerator CruiseControlCoroutine()
        {
            float prevSpeed = 0.0f;
            float targetAcceleration = 2.5f;
            float prevTime = Time.time;

            const float TIME_WAIT = 0.1f;
            float timeDelta = TIME_WAIT;

            while (running)
            {
                float speed        = Mathf.Abs(remoteControl.GetForwardSpeed() * 3.6f);
                float acceleration = (speed - prevSpeed) / timeDelta;

                targetAcceleration = MaintainSpeed(targetAcceleration, timeDelta, speed, acceleration);

                float targetThrottle       = remoteControl.GetTargetThrottle();
                float targetIndependentBrake = remoteControl.GetTargetIndependentBrake();
                float targetBrake          = remoteControl.GetTargetBrake();

                prevSpeed = speed;

                yield return new WaitForSeconds(TIME_WAIT);

                timeDelta = Time.time - prevTime;
                prevTime  = Time.time;

                if (Mathf.Abs(targetThrottle - remoteControl.GetTargetThrottle()) > 1.0f * TIME_WAIT)
                    running = false;

                if (Mathf.Abs(targetIndependentBrake - remoteControl.GetTargetIndependentBrake()) > 1.0f * TIME_WAIT
                    || Mathf.Abs(targetBrake - remoteControl.GetTargetBrake()) > 0.1f * TIME_WAIT)
                {
                    if (remoteControl.GetTargetThrottle() > Mathf.Epsilon)
                    {
                        remoteControl.UpdateThrottle(-100.0f);
                        running = false;
                    }
                }

                if (!running)
                    Module.PlayClip(Module.offClip);
            }
        }

        public void Dispose()
        {
            running = false;
        }

        // ── Static cruise control helpers (legacy, not in UI) ────────────────

        public static void ToggleCruiseControl(float? speed = null)
        {
            if (CruiseControl == null || !CruiseControl.Running)
                SetCruiseControl(speed);
            else
            {
                Module.PlayClip(Module.offClip);
                ResetCruiseControl();
            }
        }

        public static void ResetCruiseControl()
        {
            if (CruiseControl != null)
            {
                CruiseControl.Dispose();
                CruiseControl = null;
                OnCruiseControlChange?.Invoke(null, null);
            }
        }

        public static event EventHandler OnCruiseControlChange;

        public static float SetCruiseControl(float? speed = null)
        {
            TrainCar car = PlayerManager.LastLoco;
            if (car == null)
                throw new ArgumentNullException(nameof(car));

            if (!speed.HasValue)
                speed = Mathf.Abs(car.GetForwardSpeed() * 3.6f);

            if (CruiseControl != null && CruiseControl.Running)
            {
                UpdateTargetSpeed(speed.Value - CruiseControl.TargetSpeed);
                return speed.Value;
            }

            ResetCruiseControl();
            Module.PlayClip(Module.onClip);

            ILocomotiveRemoteControl remote = car.GetComponent<ILocomotiveRemoteControl>();
            CruiseControl = new LocoCruiseControl(remote, car);
            CruiseControl.StartCruiseControl(speed.Value);

            OnCruiseControlChange?.Invoke(null, null);
            return speed.Value;
        }

        public static float UpdateTargetSpeed(float speedOffset)
        {
            if (CruiseControl != null)
            {
                CruiseControl.TargetSpeed += speedOffset;
                if (CruiseControl.TargetSpeed < 0.0f)
                    CruiseControl.TargetSpeed = 0.0f;
                OnCruiseControlChange?.Invoke(null, null);
                return CruiseControl.TargetSpeed;
            }
            return 0.0f;
        }

        public static float? GetTargetSpeed()
        {
            if (CruiseControl != null)
                return CruiseControl.TargetSpeed;
            return null;
        }
    }
}
