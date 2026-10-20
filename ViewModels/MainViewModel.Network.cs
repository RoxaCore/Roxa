// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Net.NetworkInformation;
using System.Threading;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

// ---------- Reacting to network changes ----------
//
// Previously the network event was never listened to (NetworkChange appeared zero times in the whole project).
// Result: with WiFi off/on the kill switch blocked correctly, but nobody brought the tunnel
// back up, leaving the user with dead internet until they manually hit «disconnect + connect».
//
// Here we probe the physical gateway and the direct internet; when they return we reconnect to that same
// previous server automatically. If they do not return or the attempt cap runs out, we tell the user instead of staying silent.
public partial class MainViewModel
{
    // The network event fires several times within a few milliseconds (link up/down, DHCP, addresses).
    // Without debounce, every fire would run a full disconnect+connect.
    private const int NetDebounceMs = 4000;

    // After the events settle we wait this long so the link and DHCP truly come up.
    private const int NetSettleMs = 3000;

    // How many times to re-probe the gateway; 1.5 seconds each time ⇒ about 30 seconds.
    private const int NetGatewayTries = 20;
    private const int NetGatewayStepMs = 1500;

    // How many times to probe the direct internet; 3 seconds apart each time.
    // (The route bypass is created once and kept across all of these attempts.)
    private const int NetOnlineTries = 5;
    private const int NetOnlineStepMs = 3000;

    // Cap on automatic reconnects, so a flaky network cannot build an infinite loop.
    private const int NetMaxReconnects = 2;

    // If this much time has passed since the last automatic attempt, the cap counter starts over;
    // otherwise two WiFi drops in two different hours would exhaust the cap for each other.
    private const long NetTryWindowMs = 120000;

    // After each disconnect/connect done by the app itself, ignore network events this long;
    // routes and DNS change inside this same window and produce spurious events.
    private const int NetSelfChangeSuppressMs = 20000;

    // To find out whether there is a real problem or only a spurious network event,
    // we send a single probe through the tunnel's own path; this is the time we allow it.
    private const int NetTunnelCheckMs = 4000;

    private long _netEventTick;
    private long _netLastTryTick;
    // Our own disconnect/connect changes routes and DNS, which in turn fires the network event;
    // without this window an automatic reconnect would immediately start another reconnect
    // (a disconnect+connect loop every ~12 seconds, with an «internet blocked» toast in every loop).
    private long _netSuppressUntil;
    private int _netRecoverRunning;
    private int _netReconnects;
    private bool _netWatcherStarted;

    /// <summary>
    /// Ignores network events for a short while after the app changes routes by itself.
    /// </summary>
    private void SuppressNetworkWatcher(int ms) =>
        Interlocked.Exchange(ref _netSuppressUntil, Environment.TickCount64 + ms);

    /// <summary>
    /// Listens for network changes (toggling WiFi, switching modems, getting a fresh address).
    /// Called only once; these events live as long as the program itself.
    /// </summary>
    private void StartNetworkWatcher()
    {
        if (_netWatcherStarted) return;
        _netWatcherStarted = true;
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex) { AppendLog($"network watcher failed: {ex.Message}"); }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => NoteNetworkChange();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => NoteNetworkChange();

    /// <summary>
    /// Only worth doing while connected; the user's manual disconnect also removes the routes and
    /// fires this same event, and must not kick off the next automatic reconnect.
    /// </summary>
    private void NoteNetworkChange()
    {
        if (!IsConnected) return;

        // The app's own route changes (disconnect/connect, server switch) fire the network event;
        // it must not be interpreted as «the network changed».
        if (Environment.TickCount64 < Interlocked.Read(ref _netSuppressUntil)) return;

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _netLastTryTick) > NetTryWindowMs)
            Interlocked.Exchange(ref _netReconnects, 0);

        Interlocked.Exchange(ref _netEventTick, now);

        // Single-flight: if a recovery is already running, just push its timer back.
        if (Interlocked.CompareExchange(ref _netRecoverRunning, 1, 0) != 0) return;

        // We hop to the UI thread; Connect/Disconnect and the UI properties all require the UI thread.
        Post(() => _ = RecoverAfterNetworkChangeAsync());
    }

    private async Task RecoverAfterNetworkChangeAsync()
    {
        try
        {
            // 1) Wait for the events to settle; every fresh event pushes the timer back.
            while (Environment.TickCount64 - Interlocked.Read(ref _netEventTick) < NetDebounceMs)
                await Task.Delay(500);
            if (!IsConnected) return;

            // 2) Wait a few more seconds so DHCP/link stabilizes.
            await Task.Delay(NetSettleMs);
            if (!IsConnected) return;

            // 2.5) Do we even have a problem? If we can already reach the internet
            // through the tunnel's own path, then the tunnel is healthy and that network event was nothing but noise:
            // the connection itself (TUN creation, route and DNS changes), a server switch, or some
            // other trivial event. Touching a healthy connection means a needless few-second
            // outage — exactly what the user was complaining about.
            // The probe must go through the engine's own port, not directly; otherwise if the tunnel is dead but
            // the direct internet is healthy, we would wrongly call it «healthy» and abort the recovery.
            int tunnelPort = _engine.HttpPort;
            if (tunnelPort > 0)
            {
                bool tunnelHealthy;
                try { tunnelHealthy = await NetHealth.IsInternetAvailableAsync(NetTunnelCheckMs, tunnelPort); }
                catch { tunnelHealthy = false; }
                if (tunnelHealthy)
                {
                    TunManager.Log("net-change: tunnel still healthy; ignoring network event");
                    return;
                }
            }

            AppendLog(TranslationManager.Instance["Log_NetChanged"]);
            TunManager.Log("net-change: recovery starting");

            // 3) The physical gateway must come back; without it connecting is pointless.
            // This is a local read of the routing table and the kill switch's WFP block has no effect on it.
            bool gatewayBack = false;
            for (int i = 0; i < NetGatewayTries; i++)
            {
                if (!string.IsNullOrEmpty(await Task.Run(TunManager.GetDefaultGatewayVia)))
                { gatewayBack = true; break; }
                if (!IsConnected) return;
                await Task.Delay(NetGatewayStepMs);
            }
            if (!gatewayBack) { NotifyNetworkDown(); return; }

            // 4) The gateway is back; has the direct internet really returned?
            // The probe must use the forced bypass, because the current tunnel is dead and a probe along
            // that same tunnel always fails. The probes themselves are sent by Roxa.exe, which is in
            // the kill switch's permission list, so even with the WFP block it can reach the fresh network.
            // The bypass is created once and kept for all attempts; building it needs a
            // route.exe process, and repeating it five times slowed the connection down.
            bool online = false;
            try
            {
                using var bypass = await TestRouteBypass.BeginAsync(NetHealth.ProbeHosts, forceDirect: true);
                for (int i = 0; i < NetOnlineTries; i++)
                {
                    if (await NetHealth.IsInternetAvailableAsync(timeoutMs: 2500)) { online = true; break; }
                    if (!IsConnected) return;
                    await Task.Delay(NetOnlineStepMs);
                }
            }
            catch { }
            if (!online) { NotifyNetworkDown(); return; }

            // 5) The network is healthy but the old tunnel is dead; reconnect to the same server.
            if (IsConnecting || IsTesting || IsSwitchingServer) return;

            var server = _engine.CurrentServer ?? SelectedServer;
            if (server == null || !Servers.Contains(server)) return;

            int tries = Interlocked.Increment(ref _netReconnects);
            if (tries > NetMaxReconnects)
            {
                AppendLog(TranslationManager.Instance["Log_NetReconnectGaveUp"]);
                NotifyNetworkDown();
                return;
            }
            Interlocked.Exchange(ref _netLastTryTick, Environment.TickCount64);

            AppendLog(string.Format(TranslationManager.Instance["Log_NetReconnecting"], server.Name));
            TunManager.Log($"net-change: reconnecting to {server.Name} (try {tries}/{NetMaxReconnects})");
            DisconnectCore(false);
            await AwaitCleanupAsync();
            if (IsConnecting || IsTesting || IsSwitchingServer) return;

            SelectedServer = server;
            await Connect();
        }
        catch (Exception ex)
        {
            AppendLog($"network recovery failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _netRecoverRunning, 0);
        }
    }

    /// <summary>
    /// Instead of deadly silence: if automatic reconnection was impossible, the user must know they have to connect manually.
    /// </summary>
    private void NotifyNetworkDown()
    {
        string msg = TranslationManager.Instance["Toast_NetDownReconnect"];
        Status = msg;
        ToastHandler?.Invoke(msg);
        AppendLog(msg);
    }
}
