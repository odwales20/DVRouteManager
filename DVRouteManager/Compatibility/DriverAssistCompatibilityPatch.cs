using DV.Logic.Job;
using HarmonyLib;
using System;
using System.Reflection;

namespace DVRouteManager.Compatibility
{
    [HarmonyPatch]
    internal static class DriverAssistCompatibilityPatch
    {
        private static bool Prepare()
        {
            return AccessTools.TypeByName("DriverAssist.Implementation.DriverAssistController") != null;
        }

        private static MethodBase TargetMethod()
        {
            Type controllerType = AccessTools.TypeByName("DriverAssist.Implementation.DriverAssistController");
            return controllerType?.GetMethod("OnRegisterJob", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private static Exception Finalizer(Exception __exception, Job job)
        {
            if (__exception == null)
                return null;

            Module.mod?.Logger.Log($"DriverAssist job registration skipped for {job?.ID}: {__exception.GetType().Name} - {__exception.Message}");
            return null;
        }
    }
}
