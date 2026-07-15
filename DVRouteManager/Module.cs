using CommandTerminal;
using CommsRadioAPI;
using DV;
using DV.Logic.Job;
using DV.Simulation.Cars;
using DVRouteManager.CommsRadio;
using HarmonyLib;
using SimpleJson;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityAsync;
using UnityEngine;
using UnityEngine.Networking;
using UnityModManagerNet;
using static UnityModManagerNet.UnityModManager;

namespace DVRouteManager
{
#if DEBUG
    [EnableReloading]
#endif
    static class Module
    {
        public const string BUILD = "b035";
        private const string AUDIO_DIRECTORY = "audio\\";
        public static UnityModManager.ModEntry mod;
        public static Settings settings;

        public static ActiveRoute ActiveRoute { get; private set; }

        public static AudioClip stopTrainClip { get; private set; }
        public static AudioClip trainEnd { get; private set; }
        public static AudioClip wrongWayClip { get; private set; }
        public static AudioClip offClip { get; private set; }
        public static AudioClip onClip { get; private set; }
        public static AudioClip setClip { get; private set; }
        public static AudioSource generalAudioSource { get; private set; }
        private static Harmony harmony;

        public static string ModulePath
        {
            get
            {
                return mod.Path;
            }
        }

        private static Dictionary<string, LocoAI> locosAI = new Dictionary<string, LocoAI>();
        public static LocoAI TryGetLocoAI(TrainCar car)
        {
            if (car == null) return null;
            LocoAI locoAI;
            locosAI.TryGetValue(car.logicCar.ID, out locoAI);
            return locoAI;
        }

        public static LocoAI GetLocoAI(TrainCar car)
        {
            LocoAI locoAI;
            if (!locosAI.TryGetValue(car.logicCar.ID, out locoAI))
            {
                SimController simController = car.GetComponent<SimController>();
                if (simController == null || simController.controlsOverrider == null)
                {
                    throw new CommandException("Unsupported locomotive");
                }

                // Engine-on check removed; control fails naturally if engine is off

                ILocomotiveRemoteControl remote = car.GetComponent<ILocomotiveRemoteControl>();
                if (remote == null)
                {
                    // Loco has no RemoteControllerModule (e.g. DM3) — use BaseControlsOverrider directly
                    mod.Logger.Log($"No ILocomotiveRemoteControl on {car.carLivery?.id ?? car.logicCar.ID} — using ControlsOverriderRemote");
                    remote = new ControlsOverriderRemote(car, simController);
                }

                locoAI = new LocoAI(remote, car);
                locosAI.Add(car.logicCar.ID, locoAI);
            }

            return locoAI;
        }

        public class VersionInfo
        {
            public string Version;
            public string downloadUrl;
        }
        public static VersionInfo VersionForUpdate { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "<Pending>")]
        static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                settings = Settings.Load<Settings>(modEntry);
                mod = modEntry;
                mod.OnToggle = OnToggle;
                mod.OnUpdate = OnUpdate;
                mod.OnGUI = OnGUI;
                mod.OnSaveGUI = OnSaveGUI;
#if DEBUG
                modEntry.OnUnload = Unload;
#endif
                harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                AsyncManager.Initialize();

                ActiveRoute = new ActiveRoute();

                modEntry.Logger.Log($"RouteManager initialized build={Module.BUILD}");
                Terminal.Log($"[DVRouteManager] build={Module.BUILD}");
            }
            catch (Exception exc)
            {
                modEntry.Logger.LogException(exc);
            }

            return true;
        }

#if DEBUG
        static bool Unload(UnityModManager.ModEntry modEntry)
        {
            try
            {
                Deactivate();

                harmony?.UnpatchAll(modEntry.Info.Id);
                harmony = null;

                if (generalAudioSource != null)
                {
                    UnityEngine.Object.Destroy(generalAudioSource);
                    generalAudioSource = null;
                }

                stopTrainClip = null;
                trainEnd = null;
                wrongWayClip = null;
                offClip = null;
                onClip = null;
                setClip = null;
                VersionForUpdate = null;
                foreach (var locoAI in locosAI.Values)
                    locoAI?.StopAll();
                locosAI.Clear();
                ActiveRoute = null;
                commsRadioMode = null;

                mod.OnToggle = null;
                mod.OnUpdate = null;
                mod.OnGUI = null;
                mod.OnSaveGUI = null;
                mod.OnUnload = null;

                AsyncManager.Shutdown();
                modEntry.Logger.Log("RouteManager unloaded for reload");
                return true;
            }
            catch (Exception exc)
            {
                modEntry.Logger.LogException(exc);
                return false;
            }
        }
#endif

        private static void OnSaveGUI(ModEntry modEntry)
        {
            settings.Save(modEntry);
        }

        private static void OnGUI(ModEntry modEntry)
        {
            GUILayout.Label("Reversing strategy:");
            foreach (ReversingStrategy strategy in System.Enum.GetValues(typeof(ReversingStrategy)))
            {
                if (GUILayout.Toggle(settings.ReversingStrategy == strategy, strategy.ToString()))
                    settings.ReversingStrategy = strategy;
            }
        }

        public static IEnumerator CheckUpdates()
        {
            while (Terminal.Shell == null || Terminal.Autocomplete == null)
            {
                yield return null;
            }

            UnityWebRequest www = null;

            try
            {
                www = UnityWebRequest.Get(mod.Info.Repository);
                www.timeout = 5;
                www.downloadHandler = new DownloadHandlerBuffer();
            }
            catch (Exception e)
            {
                Terminal.Log(e.Message + " " + e.StackTrace);
            }

            if (www != null)
            {
                yield return www.SendWebRequest();

                while (!www.downloadHandler.isDone)
                    yield return null;

                if (!www.isHttpError && !www.isNetworkError)
                {
                    var json = (IDictionary<string, object>)SimpleJson.SimpleJson.DeserializeObject(www.downloadHandler.text);

                    JsonObject releaseInfo = ((json["Releases"] as JsonArray)?[0] as JsonObject);
                    string version = (string)releaseInfo?["Version"];
                    Version latestVersion = new Version(version);
                    Version moduleVersion = new Version(mod.Info.Version);

                    if (latestVersion > moduleVersion)
                    {
                        VersionForUpdate = new VersionInfo();
                        VersionForUpdate.Version = version;
                        VersionForUpdate.downloadUrl = (string)releaseInfo["DownloadUrl"]; ;
                        Terminal.Log($"{version} {VersionForUpdate.downloadUrl}");
                    }

                }
                else
                {
                    mod.Logger.Log($"Update check skipped: {www.error}");
                }
            }

        }
        public static IEnumerator SetupCommands()
        {
            while (Terminal.Shell == null || Terminal.Autocomplete == null)
            {
                yield return null;
            }

            stopTrainClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "stoptrain.wav", "stoptrain");
            trainEnd = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "trainend.wav", "trainend");
            wrongWayClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "wrongway.wav", "wrongway");
            onClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "on.wav", "on");
            offClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "off.wav", "off");
            setClip = AudioUtils.LoadAudioClip(AUDIO_DIRECTORY + "set.wav", "set");

            Terminal.Shell.Commands.Remove("route");
            Terminal.Shell.AddCommand("route", RouteCommand.DoTerminalCommand, 0, -1, "", null);
            CommandInfo ci = new CommandInfo();
            ci.name = "route";
            Terminal.Autocomplete.Register(ci);
            Terminal.Log("Route command registered");

            PathFinder.BuildTurntableCache();
            LocoAI.BuildSignSpeedLimitCache();
        }

        public static IEnumerator SetupAudio()
        {
            AudioListener listener = null;

            //yield return new WaitForSeconds(5.0f);

            while (listener == null)
            {
                yield return new UnityEngine.WaitForSeconds(0.5f);
                listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            }

            SetupAudioSource(listener);

#if DEBUG
            Terminal.Log($"AudioListener found {generalAudioSource}");
            mod.Logger.Log("AudioListener found");
#endif
        }

        private static void SetupAudioSource(AudioListener listener)
        {
            generalAudioSource = listener.gameObject.AddComponent<AudioSource>();
            //audioSource.outputAudioMixerGroup = Engine_Layered_Audio.audioMixerGroup;
            generalAudioSource.playOnAwake = true;
            generalAudioSource.loop = false;
            generalAudioSource.maxDistance = 300f;
            //generalAudioSource.clip = Module.stopTrainClip;
            generalAudioSource.spatialBlend = 0f;
            generalAudioSource.dopplerLevel = 0f;
            generalAudioSource.spread = 10f;
        }

        public static void PlayClip(AudioClip clip)
        {
            if (generalAudioSource == null)
            {
                AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
#if DEBUG
                Terminal.Log("PlayClip Init #2");
#endif
                SetupAudioSource(listener);
            }

            if (generalAudioSource != null && clip != null)
            {
                generalAudioSource.clip = clip;
                generalAudioSource.Play();
            }
            else if(clip == null)
            {
                Terminal.Log("Cannot play sound, clip == null");
            }
            else if (generalAudioSource == null)
            {
                Terminal.Log("Cannot play sound, generalAudioSource == null");
            }
        }

        public static Coroutine StartCoroutine(IEnumerator coroutine)
        {
            return AsyncManager.StartCoroutine(coroutine);
        }
        public static void StartCoroutines(IEnumerator[] coroutines)
        {
            foreach (var coroutine in coroutines)
            {
                AsyncManager.StartCoroutine(coroutine);
            }
        }

        private static void StartInitCoroutines()
        {
            AsyncManager.StartCoroutine(SetupCommands());
            AsyncManager.StartCoroutine(SetupAudio());
            AsyncManager.StartCoroutine(CheckUpdates());
        }

        private static void StopInitCoroutines()
        {
        }



        static bool OnToggle(UnityModManager.ModEntry _, bool active)
        {
            if (active)
            {
                StartInitCoroutines();
                AsyncManager.StartCoroutine(AddCommsRouteManagerWhenReady());
            }
            else
            {
                Deactivate();
            }

            return true;
        }

        private static void Deactivate()
        {
            Terminal.Log("RouteManager deactivating");

            RemoveCommsRouteManager();
            Terminal.Shell?.Commands.Remove("route");
            StopInitCoroutines();
            //Terminal.Autocomplete.UnRegister("route"); //currently not able unregister
            Module.ActiveRoute?.ClearRoute();
        }

        private static void OnUpdate(ModEntry arg1, float arg2)
        {
            if (Module.ActiveRoute.IsSet && Module.ActiveRoute.RouteTracker != null && Module.settings.TrainEndAlarm.Down())
            {
                Module.ActiveRoute.RouteTracker.NotifyTrainEnd();
            }

        }


        private static CommsRadioMode commsRadioMode;

        private static IEnumerator AddCommsRouteManagerWhenReady()
        {
            while (UnityEngine.Object.FindObjectOfType<CommsRadioController>() == null)
                yield return null;

            try
            {
                RemoveCommsRouteManager();
                commsRadioMode = CommsRadioMode.Create(new RouteManagerInitialState(), new Color(0.5f, 0.5f, 0.5f));
                Module.mod.Logger.Log("Comm radio mode added via CommsRadioAPI");
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("Error registering CommsRadio mode: " + e.Message);
            }
        }

        private static void RemoveCommsRouteManager()
        {
            try
            {
                var controller = UnityEngine.Object.FindObjectOfType<CommsRadioController>();
                if (controller == null)
                    return;

                var allModesField = typeof(CommsRadioController).GetField("allModes", BindingFlags.Instance | BindingFlags.NonPublic);
                if (!(allModesField?.GetValue(controller) is IList modes))
                    return;

                int removed = 0;
                for (int i = modes.Count - 1; i >= 0; i--)
                {
                    object mode = modes[i];
                    if (!IsRouteManagerCommsMode(mode))
                        continue;

                    modes.RemoveAt(i);
                    removed++;

                    if (mode is Component component)
                        UnityEngine.Object.Destroy(component);
                }

                if (removed > 0)
                {
                    controller.ReactivateModes();
                    Module.mod.Logger.Log($"Removed {removed} stale RouteManager comm radio mode(s)");
                }

                commsRadioMode = null;
            }
            catch (Exception e)
            {
                Module.mod.Logger.Log("Error removing CommsRadio mode: " + e.Message);
            }
        }

        private static bool IsRouteManagerCommsMode(object mode)
        {
            if (mode == null)
                return false;
            if (ReferenceEquals(mode, commsRadioMode))
                return true;

            Type modeType = mode.GetType();
            if (modeType.FullName != "CommsRadioAPI.CommsRadioMode")
                return false;

            return HasRouteManagerState(modeType, mode, "startingState") || HasRouteManagerState(modeType, mode, "activeState");
        }

        private static bool HasRouteManagerState(Type modeType, object mode, string fieldName)
        {
            var field = modeType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            object state = field?.GetValue(mode);
            string stateTypeName = state?.GetType().FullName;
            return stateTypeName != null && stateTypeName.StartsWith("DVRouteManager.CommsRadio.RouteManager", StringComparison.Ordinal);
        }
    }
}
