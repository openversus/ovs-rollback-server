using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    internal class Events
    {
        protected internal static event EventHandler? OnServerStart;
        protected internal static event EventHandler? OnServerStop;
        protected internal static event EventHandler? OnServerListening;
        protected internal static event EventHandler? OnHeartBeat;
        protected internal static event EventHandler? OnConfigReceived;
        protected internal static event EventHandler? OnPlayerConnect;
        protected internal static event EventHandler? OnPlayerDisconnect;
        protected internal static event EventHandler? OnAllPlayesrDisconnected;
        protected internal static event EventHandler? OnPlayerReady;
        protected internal static event EventHandler? OnAllPlayersReady;
        protected internal static event EventHandler? OnRageQuit;
        protected internal static event EventHandler? OnPingPhase;
        protected internal static event EventHandler? OnTickPerformance;
        protected internal static event EventHandler? OnMatchStart;
        protected internal static event EventHandler? OnMatchEnd;
        protected internal static event EventHandler? OnError;
        protected internal static event EventHandler? OnTerminatingError;
        protected internal static event EventHandler? OnServerIdle;

        public async static Task SendServerStartEvent(object? sender, StatusEventArgs e)
        {
            OnServerStart?.Invoke(sender, e);
        }

        public async static Task SendServerStopEvent(object? sender, StatusEventArgs e)
        {
            OnServerStop?.Invoke(sender, e);
        }
        public async static Task SendServerListeningEvent(object? sender, StatusEventArgs e)
        {
            OnServerListening?.Invoke(sender, e);
        }
        public async static Task SendHeartBeatEvent(object? sender, StatusEventArgs e)
        {
            OnHeartBeat?.Invoke(sender, e);
        }
        public async static Task SendConfigReceivedEvent(object? sender, StatusEventArgs e)
        {
            OnConfigReceived?.Invoke(sender, e);
        }
        public async static Task SendPlayerConnectEvent(object? sender, StatusEventArgs e)
        {
            OnPlayerConnect?.Invoke(sender, e);
        }

        public async static Task SendPlayerReadyEvent(object? sender, StatusEventArgs e)
        {
            OnPlayerReady?.Invoke(sender, e);
        }

        public async static Task SendAllPlayersReadyEvent(object? sender, StatusEventArgs e)
        {
            OnAllPlayersReady?.Invoke(sender, e);
        }
        public async static Task SendPlayerDisconnectEvent(object? sender, StatusEventArgs e)
        {
            OnPlayerDisconnect?.Invoke(sender, e);
        }

        public async static Task SendAllPlayersDisconnectedEvent(object? sender, StatusEventArgs e)
        {
            OnAllPlayesrDisconnected?.Invoke(sender, e);
        }
        public async static Task SendRageQuitEvent(object? sender, StatusEventArgs e)
        {
            OnRageQuit?.Invoke(sender, e);
        }
        public async static Task SendPingPhaseEvent(object? sender, StatusEventArgs e)
        {
            OnPingPhase?.Invoke(sender, e);
        }

        public async static Task SendTickPerformanceEvent(object? sender, StatusEventArgs e)
        {
            OnTickPerformance?.Invoke(sender, e);
        }

        public async static Task SendMatchStartEvent(object? sender, StatusEventArgs e)
        {
            OnMatchStart?.Invoke(sender, e);
        }

        public async static Task SendMatchEndEvent(object? sender, StatusEventArgs e)
        {
            OnMatchEnd?.Invoke(sender, e);
        }

        public async static Task SendServerIdleEvent(object? sender, StatusEventArgs e)
        {
            OnServerIdle?.Invoke(sender, e);
        }

        public async static Task SendErrorEvent(object? sender, StatusEventArgs e)
        {
            OnError?.Invoke(sender, e);
        }
        public async static Task SendTerminatingErrorEvent(object? sender, StatusEventArgs e)
        {
            OnTerminatingError?.Invoke(sender, e);
            Server.cts.Cancel();
        }

        protected internal static void OnServerStartHandler(object sender, EventArgs e) => OnServerStart?.Invoke(sender, e);
        protected internal static void OnServerStopHandler(object sender, EventArgs e) => OnServerStop?.Invoke(sender, e);
        protected internal static void OnServerListeningHandler(object sender, EventArgs e) => OnServerListening?.Invoke(sender, e);
        protected internal static void OnHeartBeatHandler(object sender, EventArgs e) => OnHeartBeat?.Invoke(sender, e);
        protected internal static void OnConfigReceivedHandler(object sender, EventArgs e) => OnConfigReceived?.Invoke(sender, e);
        protected internal static void OnPlayerConnectHandler(object sender, EventArgs e) => OnPlayerConnect?.Invoke(sender, e);
        protected internal static void OnPlayerReadyHandler(object sender, EventArgs e) => OnPlayerReady?.Invoke(sender, e);
        protected internal static void OnAllPlayersReadyHandler(object sender, EventArgs e) => OnAllPlayersReady?.Invoke(sender, e);
        protected internal static void OnPlayerDisconnectHandler(object sender, EventArgs e) => OnPlayerDisconnect?.Invoke(sender, e);
        protected internal static void OnAllPlayersDisconnectedHandler(object sender, EventArgs e) => OnAllPlayesrDisconnected?.Invoke(sender, e);
        protected internal static void OnRageQuitHandler(object sender,EventArgs e) => OnRageQuit?.Invoke(sender, e);
        protected internal static void OnPingPhaseHandler(object sender, EventArgs e) => OnPingPhase?.Invoke(sender, e);
        protected internal static void OnMatchStartHandler(object sender, EventArgs e) => OnMatchStart?.Invoke(sender, e);
        protected internal static void OnMatchEndHandler(object sender, EventArgs e) => OnMatchEnd?.Invoke(sender, e);
        protected internal static void OnErrorHandler(object sender, EventArgs e) => OnError?.Invoke(sender, e);
        protected internal static void OnTerminatingErrorHandler(object sender, EventArgs e) => OnTerminatingError?.Invoke(sender, e);
        protected internal static void OnServerIdleHandler(object sender, EventArgs e) => OnServerIdle?.Invoke(sender, e);
    }
}
