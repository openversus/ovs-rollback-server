using OVS.Rollback.Configuration;
using OVS.Rollback.Models;
using OVS.Rollback.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    internal class Events
    {
        protected internal delegate Task<MatchStatusResponse?> MatchEventHandler(StatusEventArgs args);

        private static bool shouldFireEvents = ServerConfiguration.Instance.Server.FireMatchEvents;

        protected internal static event MatchEventHandler? OnServerStart;
        protected internal static event MatchEventHandler? OnServerStop;
        protected internal static event MatchEventHandler? OnServerListening;
        protected internal static event MatchEventHandler? OnHeartBeat;
        protected internal static event MatchEventHandler? OnConfigReceived;
        protected internal static event MatchEventHandler? OnPlayerConnect;
        protected internal static event MatchEventHandler? OnPlayerDisconnect;
        protected internal static event MatchEventHandler? OnAllPlayesrDisconnected;
        protected internal static event MatchEventHandler? OnPlayerReady;
        protected internal static event MatchEventHandler? OnAllPlayersReady;
        protected internal static event MatchEventHandler? OnRageQuit;
        protected internal static event MatchEventHandler? OnPingPhase;
        protected internal static event MatchEventHandler? OnTickPerformance;
        protected internal static event MatchEventHandler? OnMatchStart;
        protected internal static event MatchEventHandler? OnMatchEnd;
        protected internal static event MatchEventHandler? OnError;
        protected internal static event MatchEventHandler? OnTerminatingError;
        protected internal static event MatchEventHandler? OnServerIdle;
        protected internal static event MatchEventHandler? OnMementoMori;

        private static void FireMatchStatusEvent(MatchEventHandler? eventHandler, object? sender, StatusEventArgs args)
        {
            // If event firing is disabled, skip invoking the event handler
            // This allows us to avoid unnecessary overhead from creating EventArgs and invoking delegates when events are not needed
            // Configurable via the JSON config file (Server.FireMatchEvents) and Environment variable (Server__FireMatchEvents)
            if (shouldFireEvents)
            {
                eventHandler?.Invoke(args);
            }
        }

        public static Task SendServerStartEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnServerStart, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendServerStopEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnServerStop, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendServerListeningEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnServerListening, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendHeartBeatEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnHeartBeat, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendConfigReceivedEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnConfigReceived, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerConnectEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnPlayerConnect, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerReadyEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnPlayerReady, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendAllPlayersReadyEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnAllPlayersReady, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPlayerDisconnectEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnPlayerDisconnect, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendAllPlayersDisconnectedEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnAllPlayesrDisconnected, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendRageQuitEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnRageQuit, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendPingPhaseEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnPingPhase, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendTickPerformanceEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnTickPerformance, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendMatchStartEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnMatchStart, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendMatchEndEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnMatchEnd, sender, e);
            if (Server.MementoMori)
            {
                Task.Delay(30000).ContinueWith(_ => {
                    SignalSender.MementoMori("Winding down normally after end of match");
                });
            }
            return Task.CompletedTask;
        }

        public static Task SendServerIdleEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnServerIdle, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendMementoMoriEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnMementoMori, sender, e);
            SignalSender.MementoMori();
            return Task.CompletedTask;
        }

        public static Task SendErrorEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnError, sender, e);
            return Task.CompletedTask;
        }

        public static Task SendTerminatingErrorEvent(object? sender, StatusEventArgs e)
        {
            FireMatchStatusEvent(OnTerminatingError, sender, e);
            Server.cts.Cancel();
            return Task.CompletedTask;
        }

        private static readonly Task<MatchStatusResponse?> _defaultResponse = Task.FromResult<MatchStatusResponse?>(null!);

        protected internal static Task<MatchStatusResponse?> OnServerStartHandler(object sender, StatusEventArgs args) => OnServerStart?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnServerStopHandler(object sender, StatusEventArgs args) => OnServerStop?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnServerListeningHandler(object sender, StatusEventArgs args) => OnServerListening?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnHeartBeatHandler(object sender, StatusEventArgs args) => OnHeartBeat?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnConfigReceivedHandler(object sender, StatusEventArgs args) => OnConfigReceived?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnPlayerConnectHandler(object sender, StatusEventArgs args) => OnPlayerConnect?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnPlayerReadyHandler(object sender, StatusEventArgs args) => OnPlayerReady?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnAllPlayersReadyHandler(object sender, StatusEventArgs args) => OnAllPlayersReady?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnPlayerDisconnectHandler(object sender, StatusEventArgs args) => OnPlayerDisconnect?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnAllPlayersDisconnectedHandler(object sender, StatusEventArgs args) => OnAllPlayesrDisconnected?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnRageQuitHandler(object sender, StatusEventArgs args) => OnRageQuit?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnPingPhaseHandler(object sender, StatusEventArgs args) => OnPingPhase?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnMatchStartHandler(object sender, StatusEventArgs args) => OnMatchStart?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnMatchEndHandler(object sender, StatusEventArgs args) => OnMatchEnd?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnErrorHandler(object sender, StatusEventArgs args) => OnError?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnTerminatingErrorHandler(object sender, StatusEventArgs args) => OnTerminatingError?.Invoke(args) ?? _defaultResponse;
        protected internal static Task<MatchStatusResponse?> OnServerIdleHandler(object sender, StatusEventArgs args) => OnServerIdle?.Invoke(args) ?? _defaultResponse;
        protected internal static void OnMementoMoriHandler(object sender, StatusEventArgs args) => OnMementoMori?.Invoke(args);
    }
}
