using OVS.Rollback.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    internal class Events
    {
        private static bool shouldFireEvents = ServerConfiguration.Instance.Server.FireMatchEvents;

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

        private static void FireEvent(EventHandler? eventHandler, object? sender, EventArgs e)
        {
            // If event firing is disabled, skip invoking the event handler
            // This allows us to avoid unnecessary overhead from creating EventArgs and invoking delegates when events are not needed
            // Configurable via the JSON config file (Server.FireMatchEvents) and Environment variable (Server__FireMatchEvents)
            if (shouldFireEvents)
            {
                eventHandler?.Invoke(sender, e);
            }
        }

        public static Task SendServerStartEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnServerStart, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendServerStopEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnServerStop, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendServerListeningEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnServerListening, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendHeartBeatEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnHeartBeat, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendConfigReceivedEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnConfigReceived, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerConnectEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnPlayerConnect, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerReadyEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnPlayerReady, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendAllPlayersReadyEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnAllPlayersReady, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerDisconnectEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnPlayerDisconnect, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendAllPlayersDisconnectedEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnAllPlayesrDisconnected, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendRageQuitEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnRageQuit, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPingPhaseEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnPingPhase, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendTickPerformanceEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnTickPerformance, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendMatchStartEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnMatchStart, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendMatchEndEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnMatchEnd, sender, e);
            if (Server.MementoMori)
            {
                Server.cts.Cancel();
            }
            return Task.CompletedTask;
        }

        public static Task SendServerIdleEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnServerIdle, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendErrorEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnError, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendTerminatingErrorEvent(object? sender, StatusEventArgs e)
        {
            FireEvent(OnTerminatingError, sender, e);
            Server.cts.Cancel();
            return Task.CompletedTask;
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
