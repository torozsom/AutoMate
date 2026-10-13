using Telemetry;

// Compose and run the private telemetry host; configuration and startup failures propagate to the process.
var app = TelemetryApplication.Build(args);
app.Run();
