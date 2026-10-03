# A P2P node for the bench: built from a framework-dependent publish (bench/p2p-bench.sh publishes first).
# Players run the node natively; this image only exists so a node can share a synthetic player's network namespace.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY out/node/ .
ENTRYPOINT ["dotnet", "OVS.Rollback.Node.dll"]
