# Configuration System Guide

## Overview

The OVS Rollback Server now supports a comprehensive configuration system with:
- ✅ **JSON configuration file** (`appsettings.json`)
- ✅ **Environment variable overrides** (for Docker/Kubernetes)
- ✅ **Hot reload** via SIGHUP signal (Linux/Mac) or file watcher
- ✅ **Command-line argument support** (backwards compatible)
- ✅ **Graceful shutdown** via SIGTERM/SIGINT

---

## Configuration File

### Location
`appsettings.json` in the same directory as the executable

### Example Configuration
```json
{
  "Server": {
    "Port": 8080,
    "MaxPlayers": 4,
    "BaseUrl": "",
    "HostName": ""
  },
  
  "Performance": {
    "SpinThresholdMicroseconds": 500,
    "UseAdaptiveSpinThreshold": true,
    "MetricsSamplingInterval": 10,
    "TargetFrameRate": 60
  },
  
  "Networking": {
    "ReceiveBufferSize": 65536,
    "SendBufferSize": 65536,
    "DscpValue": 46,
    "DontFragment": true,
    "HttpTimeoutSeconds": 5
  },
  
  "GameLogic": {
    "DisconnectTimeoutSeconds": 30,
    "MaxInputsPerFrame": 30,
    "InputHistoryFrames": 150,
    "InputCleanupInterval": 200,
    "MinimumInputFrames": 10
  },
  
  "RiftCalculation": {
    "Algorithm": "ClientMatched",
    "PingAlpha": 0.12,
    "RiftAlpha": 0.08,
    "TargetRift": 0.5,
    "MaxRiftDeviation": 10.0,
    "MaxRiftDeviationBoost": 20.0,
    "MaxRiftDeviationBoostAbove": 60.0,
    "RiftUpdateInterval": 10,
    "RiftUpdateThreshold": 500
  },
  
  "PingPhase": {
    "TotalPings": 20,
    "PingIntervalMilliseconds": 50
  },
  
  "InputValidation": {
    "EnableRateLimiting": true,
    "MaxInputsPerSecond": 120,
    "InputLookaheadFrames": 100,
    "InputLookbackFrames": 200
  },
  
  "DesyncDetection": {
    "EnableDesyncDetection": true,
    "ChecksumRetentionFrames": 300,
    "ChecksumCleanupInterval": 200,
    "MaxDesyncCount": 10
  },
  
  "Logging": {
    "MinimumLevel": "Information",
    "EnableMetrics": true,
    "EnableDebugLogs": false,
    "LogTickPerformance": true,
    "TickPerformanceInterval": 500
  }
}
```

**`GameLogic.MaxInputsPerFrame` can never usefully exceed 30.** The game client stores at most 30
inputs per player slot in each PlayerInput message and does not bounds-check, so a 31st would
overwrite another player's data inside the client. The server uses 30 whenever a higher value is
configured, and logs a warning at startup. For the same reason a match may have at most 4
team-side slots (`MaxPlayers - NumSpectators`); the server refuses a match with more.

---

## Environment Variables

### Format
Use double underscore `__` to separate section and property:
```
SECTION__PROPERTY=value
```

### Examples
```bash
# Server settings
export Server__Port=8081
export Server__MaxPlayers=2

# Performance tuning
export Performance__SpinThresholdMicroseconds=250
export Performance__UseAdaptiveSpinThreshold=false

# Game logic
export GameLogic__DisconnectTimeoutSeconds=60
export GameLogic__MaxInputsPerFrame=20

# Logging
export Logging__MinimumLevel=Debug
export Logging__EnableMetrics=false
```

### Special Environment Variables
These are also supported for backwards compatibility:
```bash
# Server URL (checked in order)
export Server__BaseUrl=https://api.example.com
export OVS_SERVER=https://api.example.com      # Legacy
export mvsi_server=https://api.example.com     # Legacy

# Port (command line takes precedence)
export PORT=8081
```

---

## Docker Usage

### Method 1: Environment Variables in docker-compose.yml
```yaml
version: '3.8'
services:
  rollback-server-1:
    image: ovs-rollback:latest
    environment:
      # Server config
      - Server__Port=8081
      - Server__BaseUrl=https://api.example.com
      
      # Performance tuning for 2-core VM
      - Performance__SpinThresholdMicroseconds=500
      - Performance__UseAdaptiveSpinThreshold=true
      - Performance__MetricsSamplingInterval=10
      
      # Game logic
      - GameLogic__DisconnectTimeoutSeconds=30
      - GameLogic__MaxInputsPerFrame=30
      
      # .NET Runtime optimizations
      - DOTNET_GCConserveMemory=5
      - DOTNET_GCHeapCount=1
      - DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=6
    
    network_mode: "host"
    cpus: "0.6"
    mem_limit: "300m"
```

### Method 2: Custom Config File in Image
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["OVSRollbackServer/OVSRollbackServer.csproj", "OVSRollbackServer/"]
RUN dotnet restore "./OVSRollbackServer/OVSRollbackServer.csproj"
COPY . .
WORKDIR "/src/OVSRollbackServer"
RUN dotnet build "./OVSRollbackServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "./OVSRollbackServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Copy custom config
COPY appsettings.production.json ./appsettings.json

ENTRYPOINT ["dotnet", "OVSRollbackServer.dll"]
```

### Method 3: Mount Config as Volume
```yaml
services:
  rollback-server-1:
    image: ovs-rollback:latest
    volumes:
      - ./configs/server1.json:/app/appsettings.json:ro
    network_mode: "host"
```

---

## Hot Reload (SIGHUP)

### Linux/Mac Usage
```bash
# Find process ID
ps aux | grep OVSRollbackServer
# or
pgrep OVSRollbackServer

# Send SIGHUP to reload config
kill -HUP <pid>

# Example with automatic PID lookup
kill -HUP $(pgrep OVSRollbackServer)
```

### What Gets Reloaded
✅ All configuration values from `appsettings.json`  
✅ Environment variable overrides  
✅ Performance settings (spin threshold, metrics sampling)  
✅ Game logic timeouts and limits  
✅ Logging levels  

❌ Server port (requires restart)  
❌ Already-started matches (new matches use new config)  
❌ Network buffer sizes (set at socket creation)  

### Monitoring Reload
```bash
# Watch logs for reload confirmation
tail -f server.log | grep -i "reload"

# Example output:
# 2025-01-15 10:30:45.123 [Information] Received SIGHUP signal, reloading configuration...
# 2025-01-15 10:30:45.456 [Information] Configuration reloaded successfully
# 2025-01-15 10:30:45.457 [Information] Active configuration: Port=8081, MaxPlayers=4, SpinThreshold=500μs, TargetFPS=60
```

---

## Command-Line Arguments

### Format
```bash
dotnet OVSRollbackServer.dll [port] [maxPlayers]
# or
dotnet OVSRollbackServer.dll [config.json]
```

### Examples
```bash
# Override port and maxPlayers
dotnet OVSRollbackServer.dll 8081 2

# Use custom config file
dotnet OVSRollbackServer.dll /etc/ovs/custom-config.json

# Use defaults from appsettings.json
dotnet OVSRollbackServer.dll
```

### Precedence Order
1. **Command-line arguments** (highest priority)
2. **Environment variables**
3. **appsettings.json**
4. **Built-in defaults** (lowest priority)

---

## Configuration Reference

### Server Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Port` | ushort | 8080 | UDP port to listen on |
| `MaxPlayers` | int | 4 | Maximum players per match (1-4) |
| `BaseUrl` | string | "" | Match service API URL |
| `HostName` | string | "" | Override hostname (empty = auto-detect) |

### Performance Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `SpinThresholdMicroseconds` | int | 500 | Final spin duration before tick deadline |
| `UseAdaptiveSpinThreshold` | bool | true | Auto-adjust spin based on CPU cores |
| `MetricsSamplingInterval` | int | 10 | Record histogram every N ticks |
| `TargetFrameRate` | int | 60 | Target game tick rate (Hz) |

**Tuning Recommendations:**
- **2-core VM:** `SpinThresholdMicroseconds=500`, `UseAdaptiveSpinThreshold=true`
- **4-8 core:** `SpinThresholdMicroseconds=2000`, `UseAdaptiveSpinThreshold=true`
- **16+ core:** `SpinThresholdMicroseconds=2000`, `UseAdaptiveSpinThreshold=false`

### Networking Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `ReceiveBufferSize` | int | 65536 | UDP socket receive buffer (bytes) |
| `SendBufferSize` | int | 65536 | UDP socket send buffer (bytes) |
| `DscpValue` | int | 46 | DSCP marking for QoS (EF = 46) |
| `DontFragment` | bool | true | Set DF bit in IP header |
| `HttpTimeoutSeconds` | int | 5 | HTTP client timeout for match service |

### Game Logic Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `DisconnectTimeoutSeconds` | int | 30 | Disconnect player after N seconds no input |
| `MaxInputsPerFrame` | byte | 30 | Maximum inputs sent per tick |
| `InputHistoryFrames` | uint | 150 | Frames of input history to retain |
| `InputCleanupInterval` | uint | 200 | Clean old inputs every N frames |
| `MinimumInputFrames` | int | 10 | Minimum inputs before starting tick |
| `MissToleranceFrames` | uint | 10 | Ticks to resend a peer's last acked input before predicting |

### Rift Calculation Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Algorithm` | string | ClientMatched | `ClientMatched` (see below) or `Legacy` (the original OVS smoothing). An unrecognised name falls back to `ClientMatched` |
| `PingAlpha` | float | 0.15 | Ping smoothing factor (0-1, lower = smoother). Clients are sent the smoothed ping |
| `ReportedPing` | string | Raw | Ping sent to clients, which sets their input delay (they only ever raise it): `Raw` (latest round trip, the original behaviour), `Smoothed` (SmoothedPing: fewer spikes, so usually less input delay), or `Peak` (highest round trip over the last `PeakPingWindow` acks) |
| `PeakPingWindow` | uint | 60 | Peak only: how many recent round trips to take the maximum over (at most 256) |
| `RiftAlpha` | float | 0.08 | Rift smoothing factor (0-1, lower = smoother) |
| `TargetRift` | float | 0.5 | Frames ahead of the server a client is steered to |
| `MaxRiftDeviation` | float | 10.0 | Normal limit on the rift sent to clients (frames) |
| `MaxRiftDeviationBoost` | float | 20.0 | Limit allowed while the error exceeds `MaxRiftDeviationBoostAbove`. Must stay below 50 |
| `MaxRiftDeviationBoostAbove` | float | 60.0 | Error (frames) above which the boost limit applies |
| `UseAggressiveCorrection` | bool | true | Legacy only: snap toward the target when converging |
| `RiftUpdateInterval` | uint | 10 | Legacy: update rift every N frames after the threshold. Also the cadence of rift logs and metrics for both algorithms |
| `RiftUpdateThreshold` | uint | 500 | Legacy: update every frame until this frame |
| `HysteresisEnter` / `HysteresisExit` | float | 1.25 / 0.75 | ClientMatched: start sending a correction above Enter, send exactly 0 once below Exit |
| `SmallErrorAlpha` / `SmallErrorBelow` | float | 0.2 / 3.0 | ClientMatched: smoothing for errors under SmallErrorBelow frames |

**What the client does with the rift.** The game client resets its clock correction on every
PlayerInput and derives it from that message's rift alone. It ignores anything within ±1 frame,
corrects in proportion to the rift up to 10 frames, triples its gain above 10, and disconnects above
50. It keeps correcting at the last value it was sent, so a stale value overshoots. Hence the limit
of 10: above it corrections overshoot, sometimes by 20 frames or more. The boost to 20 applies only
while a client is still far off (more than 60 frames), so long stalls are recovered as fast as before.
`ClientMatched` sends a fresh value every frame, replaces the smoothed value when the error changes
sign, and holds the client's deadband with hysteresis. It was chosen by simulating the client's
correction law against production logs (details in the reverse-engineering archive, `rift.md`).

**Tuning Recommendations:**
- **Stable connections:** `PingAlpha=0.05`, `RiftAlpha=0.03` (smoother)
- **Unstable connections:** `PingAlpha=0.2`, `RiftAlpha=0.1` (more responsive)

### Ping Phase Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `TotalPings` | uint | 20 | Number of ping requests during matchmaking |
| `PingIntervalMilliseconds` | int | 50 | Interval between ping requests |

### Input Validation Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `EnableRateLimiting` | bool | true | Enable anti-spam rate limiting |
| `MaxInputsPerSecond` | int | 120 | Maximum inputs per player per second |
| `InputLookaheadFrames` | uint | 100 | Reject inputs this far in future |
| `InputLookbackFrames` | uint | 200 | Reject inputs this far in past |

### Desync Detection Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `EnableDesyncDetection` | bool | true | Enable checksum-based desync detection |
| `ChecksumRetentionFrames` | uint | 300 | Keep checksums for N frames |
| `ChecksumCleanupInterval` | uint | 200 | Clean old checksums every N frames |
| `MaxDesyncCount` | int | 10 | Kick player after N desyncs (0 = never) |

### Logging Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `MinimumLevel` | string | "Information" | Minimum log level (Trace/Debug/Information/Warning/Error) |
| `EnableMetrics` | bool | true | Enable OpenTelemetry metrics export |
| `EnableDebugLogs` | bool | false | Enable verbose debug logging |
| `LogTickPerformance` | bool | true | Log tick performance metrics |
| `TickPerformanceInterval` | int | 500 | Log performance every N ticks |

---

## Testing Configuration

### Test 1: Verify Config Loading
```bash
# Run with debug logs
export Logging__MinimumLevel=Debug
dotnet run

# Check logs for:
# "Loaded configuration from appsettings.json"
# "Environment variables applied to configuration"
# "Active configuration: Port=8080, MaxPlayers=4, ..."
```

### Test 2: Verify Environment Override
```bash
# Set environment variable
export Server__Port=9999

# Run server
dotnet run

# Should see: "Listening on UDP port 9999"
```

### Test 3: Verify Hot Reload (Linux/Mac)
```bash
# Start server
dotnet run &
PID=$!

# Edit appsettings.json (change SpinThresholdMicroseconds)
nano appsettings.json

# Send SIGHUP
kill -HUP $PID

# Check logs: "Configuration reloaded successfully"
```

### Test 4: Verify Docker Environment
```bash
# Start with docker-compose
docker-compose up -d rollback-server-1

# Check environment variables applied
docker logs rollback-server-1 | grep "Active configuration"

# Should show overridden values from environment
```

---

## Production Recommendations

### For 2-Core VM (Your Setup)
```json
{
  "Performance": {
    "SpinThresholdMicroseconds": 500,
    "UseAdaptiveSpinThreshold": true,
    "MetricsSamplingInterval": 10
  },
  "GameLogic": {
    "DisconnectTimeoutSeconds": 30,
    "MaxInputsPerFrame": 30
  },
  "Logging": {
    "MinimumLevel": "Information",
    "EnableMetrics": true,
    "LogTickPerformance": true,
    "TickPerformanceInterval": 1000
  }
}
```

### For 16-Core Server
```json
{
  "Performance": {
    "SpinThresholdMicroseconds": 2000,
    "UseAdaptiveSpinThreshold": false,
    "MetricsSamplingInterval": 5
  },
  "GameLogic": {
    "DisconnectTimeoutSeconds": 30,
    "MaxInputsPerFrame": 30
  },
  "Logging": {
    "MinimumLevel": "Warning",
    "EnableMetrics": true,
    "LogTickPerformance": false
  }
}
```

### For Low-Latency Competitive
```json
{
  "Performance": {
    "SpinThresholdMicroseconds": 2000,
    "UseAdaptiveSpinThreshold": false,
    "TargetFrameRate": 60
  },
  "RiftCalculation": {
    "PingAlpha": 0.2,
    "RiftAlpha": 0.1,
    "MaxRiftDeviation": 10.0
  },
  "Networking": {
    "DscpValue": 46,
    "DontFragment": true
  }
}
```

---

## Troubleshooting

### "Configuration file not found"
**Cause:** `appsettings.json` missing  
**Solution:** Server will use defaults, check logs for "using defaults"

### "Failed to reload configuration"
**Cause:** Invalid JSON in config file  
**Solution:** Validate JSON syntax, check logs for parse error

### "SIGHUP not working"
**Cause:** Windows doesn't support SIGHUP  
**Solution:** Restart server or use file watcher (future feature)

### "Environment variables not applied"
**Cause:** Wrong format or typo in variable name  
**Solution:** Use exact format `Section__Property`, check logs with `Debug` level

### "Port already in use"
**Cause:** Another instance running or config override conflict  
**Solution:** Check `netstat -tulpn | grep <port>`, verify environment variables

---

## Summary

### Key Features
- ✅ **Flexible:** JSON file + environment variables + command line
- ✅ **Hot reload:** SIGHUP on Linux/Mac (no restart needed)
- ✅ **Docker-friendly:** Easy to override values per container
- ✅ **Validated:** All values type-checked and range-validated
- ✅ **Logged:** Configuration dump on startup and reload

### Quick Start
1. Copy `appsettings.json` to your deployment directory
2. Edit values as needed for your environment
3. Or set environment variables in Docker/Kubernetes
4. Run server, check logs for "Active configuration"
5. Test with `kill -HUP $(pgrep OVSRollbackServer)` to reload

### Integration with CPU Optimizations
All Phase 1 and Phase 2 optimizations are now configurable:
- **Spin threshold:** `Performance__SpinThresholdMicroseconds`
- **Adaptive spin:** `Performance__UseAdaptiveSpinThreshold`
- **Metrics sampling:** `Performance__MetricsSamplingInterval`
- **Tick performance logging:** `Logging__TickPerformanceInterval`

---

*Last updated: 2025-01-15*  
*Status: Ready for production*  
*Works with: Phase 1 + Phase 2 CPU optimizations*
