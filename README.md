# HWMonitor

A lightweight Windows desktop hardware monitor. It samples Windows CPU and
memory statistics, logical-drive capacity, and sensors exposed by
LibreHardwareMonitor. Readings are held in memory for the lifetime of the
application; no telemetry is written to disk.

## Build and run

Open `HWMonitor.csproj` in Visual Studio, or run:

```powershell
dotnet run --project .\HWMonitor.csproj
```

LibreHardwareMonitor may require elevated privileges for some low-level sensor
readings. Hardware, firmware, drivers, and CPU architecture determine which
sensors are available; unavailable values are not represented as zero.
The default sampling rate is 10 Hz. On the Graphs tab, enter a time-window
duration in seconds and use the horizontal scrollbar to navigate the session.
The graph follows new samples while the scrollbar is at its rightmost position;
scrolling back pauses that follow behavior until you return to the right edge.

## Self-contained publishing

Publish architecture-specific executables with the .NET 9 SDK:

```powershell
dotnet publish .\HWMonitor.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\HWMonitor.csproj -c Release -r win-arm64 --self-contained true
dotnet publish .\HWMonitor.csproj -c Release -r win-x86 --self-contained true
```

Each publish output is self-contained and single-file. Sensor support on ARM64
and x86 depends on the underlying LibreHardwareMonitor hardware support; the
dashboard and Windows CPU/memory readings do not depend on those sensors.
