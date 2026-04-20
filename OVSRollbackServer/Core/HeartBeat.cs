using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;

namespace OVS.Rollback.Core
{
    internal class HeartBeat : IAsyncDisposable
    {
        private readonly Stopwatch _stopwatch;

        public HeartBeat()
        {
            _stopwatch = Server.RunTimer;
        }
        protected internal async Task StartLoop()
        {
            if (!Server.RunTimer.IsRunning)
            {
                Server.RunTimer.Start();
            }

            bool firstHeartBeat = true;

            while (!Server.cts.IsCancellationRequested)
            {
                long elapsedMs = _stopwatch.ElapsedMilliseconds;
                string descriptionText = String.Empty;
                if  (firstHeartBeat)
                {
                    descriptionText = $"Server heartbeat loop started. Server has been alive for: {elapsedMs}ms ({elapsedMs / 1000}s)";
                    firstHeartBeat = false;
                }
                else
                {
                    descriptionText = $"Server heartbeat. Server has been alive for: {elapsedMs}ms ({elapsedMs / 1000}s)";   
                }

                _ = Events.SendHeartBeatEvent(this, StatusEventArgs.CreateNew(
                        description: "HeartBeat",
                        matchEvent: "HeartBeat",
                        matchDescription: descriptionText
                        )
                    );

                if (Server.MementoMori && _stopwatch.ElapsedMilliseconds >= 510000)
                {
                    _ = Events.SendMementoMoriEvent(this, StatusEventArgs.CreateNew(
                            description: "HeartBeat",
                            matchEvent: "MementoMori",
                            matchDescription: $"The server has been alive for over 8.5 minutes. Memento Mori."
                            )
                        );
                }

                await Task.Delay(60000);
            }

            // Just self-destruct when cancellation is requested so we don't need to do anything else anywhere else in the codebase to stop this loop
            await DisposeAsync();
        }

        protected internal async Task StopLoop()
        {
            long elapsedMs = _stopwatch.ElapsedMilliseconds;

            _ = Events.SendHeartBeatEvent(this, StatusEventArgs.CreateNew(
                    description: "HeartBeat",
                    matchEvent: "HeartBeat",
                    matchDescription: $"Server heartbeat loop stopped. Server heartbeat loop was alive for: {elapsedMs}ms ({elapsedMs / 1000}s)"
                    )
                );
        }

        public async ValueTask DisposeAsync()
        {
            await StopLoop();
        }
    }
}
