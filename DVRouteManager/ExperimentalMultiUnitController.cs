using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVRouteManager
{
    internal sealed class ExperimentalMultiUnitController
    {
        private const float VERIFY_TOLERANCE = 0.08f;
        private const float DM3_MAX_SPEED_KMH = 65f;

        private readonly TrainCar _lead;
        private readonly List<Unit> _units = new List<Unit>();
        private float _nextRefreshTime;
        private int _lastLoggedCount = -1;
        private bool _wasEnabled;

        private sealed class Unit
        {
            public TrainCar Car;
            public BaseControlsOverrider Controls;
            public bool OppositeOrientation;
        }

        public ExperimentalMultiUnitController(TrainCar lead)
        {
            _lead = lead;
        }

        public float ApplyConsistSpeedCap(float requestedKmh)
        {
            if (!IsEnabled)
                return requestedKmh;

            RefreshUnits();
            bool containsDm3 = IsDm3(_lead);
            for (int i = 0; i < _units.Count && !containsDm3; i++)
                containsDm3 = IsDm3(_units[i].Car);

            if (!containsDm3 || Mathf.Abs(requestedKmh) <= DM3_MAX_SPEED_KMH)
                return requestedKmh;
            return Mathf.Sign(requestedKmh) * DM3_MAX_SPEED_KMH;
        }

        public void SyncFromLead()
        {
            if (!IsEnabled)
            {
                if (_wasEnabled)
                    StopSecondaryPower();
                _wasEnabled = false;
                return;
            }
            _wasEnabled = true;

            if (_lead?.SimController?.controlsOverrider == null)
                return;

            RefreshUnits();
            BaseControlsOverrider lead = _lead.SimController.controlsOverrider;
            float throttle = Read(lead.Throttle);
            float dynamicBrake = Read(lead.DynamicBrake);
            float independentBrake = Read(lead.IndependentBrake);
            float sander = Read(lead.Sander);
            float reverser = Read(lead.Reverser, 0.5f);

            foreach (Unit unit in _units)
            {
                Write(unit.Controls?.Throttle, throttle);
                Write(unit.Controls?.DynamicBrake, dynamicBrake);
                Write(unit.Controls?.IndependentBrake, independentBrake);
                Write(unit.Controls?.Sander, sander);
                Write(unit.Controls?.Reverser, unit.OppositeOrientation ? 1f - reverser : reverser);
            }
        }

        public void StopSecondaryPower()
        {
            if (_units.Count == 0)
                return;
            foreach (Unit unit in _units)
            {
                Write(unit.Controls?.Throttle, 0f);
                Write(unit.Controls?.DynamicBrake, 0f);
            }
        }

        private bool IsEnabled => Module.settings?.EnableExperimentalMultiUnitAI == true;

        private void RefreshUnits()
        {
            if (Time.time < _nextRefreshTime)
                return;
            _nextRefreshTime = Time.time + 1f;
            _units.Clear();

            if (_lead?.trainset?.cars == null)
                return;

            var ownedControls = new HashSet<int>();
            BaseControlsOverrider leadControls = _lead.SimController?.controlsOverrider;
            if (leadControls != null)
                ownedControls.Add(leadControls.GetInstanceID());

            foreach (TrainCar car in _lead.trainset.cars)
            {
                if (car == null || car == _lead || !car.IsLoco)
                    continue;

                BaseControlsOverrider controls = car.SimController?.controlsOverrider;
                if (controls == null || !ownedControls.Add(controls.GetInstanceID()))
                    continue;

                _units.Add(new Unit
                {
                    Car = car,
                    Controls = controls,
                    OppositeOrientation = Vector3.Dot(_lead.transform.forward, car.transform.forward) < 0f
                });
            }

            if (_lastLoggedCount != _units.Count)
            {
                Module.mod?.Logger?.Log($"Experimental multi-unit AI: lead {_lead.ID}, secondary locomotives {_units.Count}");
                _lastLoggedCount = _units.Count;
            }
        }

        private static bool IsDm3(TrainCar car)
        {
            if (car == null)
                return false;
            string id = (car.carLivery?.parentType?.id ?? string.Empty) + " " + car.carType;
            return id.IndexOf("DM3", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float Read(OverridableBaseControl control, float fallback = 0f)
        {
            try { return control != null ? control.Value : fallback; }
            catch { return fallback; }
        }

        private static void Write(OverridableBaseControl control, float value)
        {
            if (control == null)
                return;

            float target = Mathf.Clamp01(value);
            try
            {
                control.Set(target);
                if (Mathf.Abs(control.Value - target) <= VERIFY_TOLERANCE)
                    return;
                control.MUOverride(target, false);
            }
            catch (Exception e)
            {
                Module.mod?.Logger?.Log("Experimental multi-unit control write failed: " + e.Message);
            }
        }
    }
}
