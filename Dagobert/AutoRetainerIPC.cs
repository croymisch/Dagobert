using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dagobert
{
    public class AutoRetainerIPC
    {
        public const string Name = "AutoRetainer";
        private const string PlusName = "AutoRetainer_Plus";

        public const string OnRetainerListCustomTask = "AutoRetainer.OnRetainerListCustomTask";
        private const string RetainerProcessingCompletedIpc = "AutoRetainer.OnRetainerProcessingCompleted";
        public static bool Installed => Svc.PluginInterface.InstalledPlugins.Any(x => (x.InternalName == Name || x.InternalName == PlusName) && x.IsLoaded);

        public static bool TryGetProtectionList(out HashSet<uint> itemIds)
        {
            itemIds = [];
            var pluginName = Svc.PluginInterface.InstalledPlugins
                .FirstOrDefault(x => (x.InternalName == Name || x.InternalName == PlusName) && x.IsLoaded)
                ?.InternalName;
            if (pluginName == null)
            {
                Svc.Log.Error("Cannot retrieve AutoRetainer's protection list because AutoRetainer is not loaded");
                return false;
            }

            try
            {
                var protectionList = Svc.PluginInterface
                    .GetIpcSubscriber<List<uint>>($"{pluginName}.PluginState.GetProtectionList")
                    .InvokeFunc();
                if (protectionList == null)
                {
                    Svc.Log.Error($"AutoRetainer ({pluginName}) returned a null protection list");
                    return false;
                }

                itemIds = [.. protectionList.Where(itemId => itemId != 0).Distinct()];
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Failed to retrieve AutoRetainer's protection list");
                return false;
            }
        }

        private static AutoRetainerIPC? _instance;
        public static AutoRetainerIPC? Instance => _instance;
        public static event Action? RetainerProcessingCompleted;

        #nullable disable
        [EzIPC] public readonly Func<bool> GetSuppressed;
        [EzIPC] public readonly Action<bool> SetSuppressed;
        [EzIPC] public readonly Func<bool> GetMultiModeEnabled;
        [EzIPC] public readonly Action<bool> SetMultiModeEnabled;
        #nullable enable

        private static AutoRetainerIPC? EnsureInstance()
        {
            if (_instance == null && Installed)
                _instance = new AutoRetainerIPC();
            return _instance;
        }

        public static bool Suppressed()
        {
            try
            {
                return EnsureInstance()?.GetSuppressed() ?? false;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Failed to read AutoRetainer suppression state");
                return false;
            }
        }

        public static bool Suppressed(bool value)
        {
            try
            {
                var instance = EnsureInstance();
                if (instance == null)
                    return false;
                instance.SetSuppressed(value);
                var actual = Suppressed();
                Svc.Log.Information($"Set AutoRetainer suppression to {value}; confirmed={actual}");
                return actual == value;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"Failed to set AutoRetainer suppression to {value}");
                return false;
            }
        }

        public static bool MultiModeEnabled()
        {
            try
            {
                return EnsureInstance()?.GetMultiModeEnabled() ?? false;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Failed to read AutoRetainer MultiMode state");
                return false;
            }
        }

        public static bool MultiModeEnabled(bool value)
        {
            try
            {
                var instance = EnsureInstance();
                if (instance == null)
                    return false;
                instance.SetMultiModeEnabled(value);
                var actual = MultiModeEnabled();
                Svc.Log.Information($"Set AutoRetainer MultiMode to {value}; confirmed={actual}");
                return actual == value;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"Failed to set AutoRetainer MultiMode to {value}");
                return false;
            }
        }

        private AutoRetainerIPC() => EzIPC.Init(this, Name);

        public static void Initialize()
        {
            if (Installed)
                _instance = new AutoRetainerIPC();
            Svc.PluginInterface
                    .GetIpcSubscriber<object>(RetainerProcessingCompletedIpc)
                    .Subscribe(OnRetainerProcessingCompletedHandler);
        }

        private static void OnRetainerProcessingCompletedHandler()
        {
            Svc.Log.Information("AutoRetainer finished processing retainers");
            var handler = RetainerProcessingCompleted;
            if (handler == null)
            {
                Svc.Log.Error("No Dagobert retainer-processing-completed handler is subscribed");
                return;
            }

            try
            {
                handler.Invoke();
                Svc.Log.Information($"After completion handler: suppressed={Suppressed()}, MultiMode={MultiModeEnabled()}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Dagobert retainer-processing-completed handler failed");
            }
        }

        public static void Dispose()
        {
            Svc.PluginInterface
                .GetIpcSubscriber<object>(RetainerProcessingCompletedIpc)
                .Unsubscribe(OnRetainerProcessingCompletedHandler);
            _instance = null;
        }
    }
}
