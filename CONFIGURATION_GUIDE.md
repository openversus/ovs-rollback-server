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
    "PingAlpha": 0.1,
    "RiftAlpha": 0.05,
    "MaxRiftDeviation": 20.0,
    "RiftUpdateInterval": 60,
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

### Rift Calculation Settings
| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `PingAlpha` | float | 0.1 | Ping smoothing factor (0-1, lower = smoother) |
| `RiftAlpha` | float | 0.05 | Rift smoothing factor (0-1, lower = smoother) |
| `MaxRiftDeviation` | float | 20.0 | Maximum rift value (frames) |
| `RiftUpdateInterval` | uint | 60 | Update rift every N frames |
| `RiftUpdateThreshold` | uint | 500 | Start updates after N frames |

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
