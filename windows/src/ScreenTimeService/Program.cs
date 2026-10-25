using ScreenTimeService;

// The Windows counterpart of the Mac root daemon: one LocalSystem service
// per PC that tracks every managed kid's minutes, reports them to Home
// Assistant over MQTT, and locks whoever is at the console when their
// retained `allowed` topic says they're blocked. See windows/README.md.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ScreenTimeEnforcer");
builder.Services.AddHostedService<EnforcerWorker>();
builder.Logging.ClearProviders(); // the service writes its own log, see Log.cs
builder.Build().Run();

internal sealed class EnforcerWorker(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceConfig config;
        try
        {
            config = ServiceConfig.Load(Paths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // Exit non-zero so the service's recovery settings restart it
            // after the config is fixed, instead of running unenforced.
            Log.Error($"Can't load {Paths.ConfigFile}.", ex);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        Log.Info($"Screen Time Enforcer {Discovery.Version} starting (device '{config.DeviceId}', fail_mode={config.FailMode}, dry_run={config.DryRun}).");
        var registry = new ManagedUserRegistry(config.ManagedUsers);
        var shared = new SharedState();
        await using var mqtt = new MqttLink(config, registry, shared);
        using var helper = new SessionHelper();
        var enforcer = new Enforcer(config, registry, shared, mqtt, helper);
        await mqtt.StartAsync();

        Log.Info("Starting console-user + lock-state detection loop.");
        // A dedicated thread: each tick blocks on Win32 calls and, when
        // locking, for up to a few seconds.
        await Task.Factory.StartNew(() =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    enforcer.Tick();
                }
                catch (Exception ex)
                {
                    // One bad tick shouldn't stop enforcement for good.
                    Log.Error("Tick failed.", ex);
                }
                stoppingToken.WaitHandle.WaitOne(Enforcer.PollInterval);
            }
        }, TaskCreationOptions.LongRunning);

        Log.Info("Stopping.");
        enforcer.Shutdown();
    }
}
