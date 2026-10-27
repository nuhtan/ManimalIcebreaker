using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // Soft Fika detection: no direct compile-time reference, so the mod still builds
    // when Fika is not installed. This is only meant to answer "who actually owns
    // the bot authority in this session?" across older and newer Fika layouts.
    internal static class FikaBridge
    {
        private const string FikaGuid = "com.fika.core";

        private static bool _resolved;
        private static bool _present;
        private static PropertyInfo _isServer;
        private static PropertyInfo _clientType;
        private static PropertyInfo _isHeadless;
        private static PropertyInfo _raidCode;
        private static PropertyInfo _serverGuid;
        private static bool _warnedBroken;

        private static MethodInfo _tryGetCoop;
        private static PropertyInfo _humanList;
        private static object _handler;
        private static readonly List<Player> Scratch = new();

        internal static bool Present
        {
            get
            {
                Resolve();
                return _present;
            }
        }

        // Newer Fika exposes headless state directly; older versions may not.
        internal static bool CanRender
        {
            get
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                    return false;

                Resolve();

                try
                {
                    if (_isHeadless != null)
                        return !(bool)_isHeadless.GetValue(null);
                    return true;
                }
                catch
                {
                    return true;
                }
            }
        }

        // True when THIS instance runs the real bot AI.
        // Solo SPT: true
        // Fika host: true
        // Fika client: false
        // If the role contract is missing or changed, fail closed rather than silently
        // spawning duplicate ghost crews.
        internal static bool BotsAuthority
        {
            get
            {
                Resolve();
                if (!_present)
                    return true;

                try
                {
                    // Newer Fika: explicit role value on FikaBackendUtils.ClientType.
                    if (_clientType != null)
                    {
                        var value = _clientType.GetValue(null);
                        if (value != null)
                        {
                            // Handles enum-backed values like EClientType.Host / Client or int values.
                            try
                            {
                                var raw = Convert.ToInt32(value);
                                if (raw == 2) return true;  // Host
                                if (raw == 1) return false; // Client
                            }
                            catch
                            {
                                // Also handle string-like enum names
                                var name = value.ToString();
                                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                                    return true;
                                if (name.Equals("Client", StringComparison.OrdinalIgnoreCase))
                                    return false;
                            }
                        }
                    }

                    // Older Fika: legacy IsServer flag.
                    if (_isServer != null)
                    {
                        var v = _isServer.GetValue(null);
                        if (v is bool b)
                            return b;
                    }

                    // Final fallback: if a CoopHandler exists, treat it as host-side authority
                    // when the game is actually in coop.
                    if (_tryGetCoop != null)
                    {
                        object[] args = { null };
                        try
                        {
                            var ok = (bool)_tryGetCoop.Invoke(null, args);
                            if (ok && args[0] != null)
                                return true;
                        }
                        catch
                        {
                            // ignore and continue
                        }
                    }

                    // Not in Fika coop, so this is effectively solo semantics.
                    var game = Singleton<AbstractGame>.Instance;
                    if (game != null && !game.GetType().Name.Contains("Coop"))
                        return true;

                    // Unknown Fika role contract: fail safely and report.
                    if (!_warnedBroken)
                    {
                        _warnedBroken = true;
                        Plugin.Log.LogError("[Fika] present but no supported bot-authority contract was found — crew authority is disabled for this session.");
                    }
                    return false;
                }
                catch
                {
                    if (!_warnedBroken)
                    {
                        _warnedBroken = true;
                        Plugin.Log.LogError("[Fika] bot-authority probe failed — crew authority disabled for this session.");
                    }
                    return false;
                }
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;

            _resolved = true;

            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FikaGuid))
                    return;

                _present = true;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Fika.Core")
                        continue;

                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "FikaBackendUtils")
                        {
                            _isServer = t.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
                            _clientType = t.GetProperty("ClientType", BindingFlags.Public | BindingFlags.Static);
                            _isHeadless = t.GetProperty("IsHeadless", BindingFlags.Public | BindingFlags.Static);
                            _raidCode = t.GetProperty("RaidCode", BindingFlags.Public | BindingFlags.Static);
                            _serverGuid = t.GetProperty("ServerGuid", BindingFlags.Public | BindingFlags.Static);
                        }
                        else if (t.Name == "CoopHandler")
                        {
                            _tryGetCoop = t.GetMethod("TryGetCoopHandler", BindingFlags.Public | BindingFlags.Static);
                            _humanList = t.GetProperty("HumanPlayers", BindingFlags.Public | BindingFlags.Instance);
                        }
                    }

                    break;
                }

                Plugin.Log.LogWarning(
                    $"[Fika] detected (IsServer {(_isServer != null ? "ok" : "MISSING")}, " +
                    $"ClientType {(_clientType != null ? "ok" : "MISSING")}, " +
                    $"CoopHandler {(_tryGetCoop != null && _humanList != null ? "ok" : "MISSING")})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Fika] detection failed (assuming solo): {ex.Message}");
            }
        }

        // Shared per-raid RNG for deterministic sync across clients without networked packets.
        // The host generates RaidCode + ServerGuid, and clients receive them before the raid starts,
        // so hashing the tuple produces identical per-raid randomness without extra packets.
        internal static System.Random SharedRaidRng(string salt)
        {
            Resolve();
            if (!_present)
                return null;

            try
            {
                var code = _raidCode?.GetValue(null) as string;
                var guid = _serverGuid?.GetValue(null)?.ToString();
                if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(guid))
                {
                    Plugin.Log.LogWarning("[Fika] RaidCode/ServerGuid unavailable — shared-seed sync degraded to local random (peers may desync). Report with your fika version.");
                    return null;
                }

                unchecked
                {
                    uint h = 2166136261;
                    foreach (char c in $"{code}|{guid}|{salt}")
                        h = (h ^ c) * 16777619;
                    return new System.Random((int)h);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Fika] shared-seed derivation failed: {e.Message}");
                return null;
            }
        }

        // ---- Human roster (used by map trigger checks) ----
        private static System.Collections.IEnumerable HumanListOrNull()
        {
            Resolve();

            if (!_present || _tryGetCoop == null || _humanList == null)
                return null;

            if (_handler is UnityEngine.Object u && !u)
                _handler = null;

            if (_handler == null)
            {
                var args = new object[] { null };
                try
                {
                    _handler = (bool)_tryGetCoop.Invoke(null, args) ? args[0] : null;
                }
                catch
                {
                    _handler = null;
                }
            }

            if (_handler == null)
                return null;

            try
            {
                return _humanList.GetValue(_handler) as System.Collections.IEnumerable;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsHeadlessBody(Player p)
        {
            try
            {
                return p.Profile?.Info?.Nickname != null &&
                       p.Profile.Info.Nickname.StartsWith("headless_", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static void CollectHumans(List<Player> into)
        {
            into.Clear();

            var list = HumanListOrNull();
            if (list != null)
            {
                foreach (var o in list)
                {
                    if (o is Player p && p != null && p.HealthController != null &&
                        p.HealthController.IsAlive && !IsHeadlessBody(p))
                    {
                        into.Add(p);
                    }
                }
            }

            if (into.Count == 0)
            {
                var main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main != null)
                    into.Add(main);
            }
        }

        internal static bool AnyHumanIn(Bounds b)
        {
            CollectHumans(Scratch);
            for (int i = 0; i < Scratch.Count; i++)
            {
                if (b.Contains(Scratch[i].Position))
                    return true;
            }
            return false;
        }

        internal static float NearestHumanSqr(Vector3 pos)
        {
            CollectHumans(Scratch);

            float best = float.MaxValue;
            for (int i = 0; i < Scratch.Count; i++)
            {
                float d = (Scratch[i].Position - pos).sqrMagnitude;
                if (d < best)
                    best = d;
            }
            return best;
        }

        internal static bool IsHumanPlayer(Player p)
        {
            if (p == null)
                return false;

            if (IsHeadlessBody(p))
                return false;

            if (p.IsYourPlayer)
                return true;

            var list = HumanListOrNull();
            if (list != null)
            {
                foreach (var o in list)
                {
                    if (ReferenceEquals(o, p))
                        return true;
                }
            }

            return false;
        }
    }
}
