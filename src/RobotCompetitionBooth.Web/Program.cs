using RobotCompetitionBooth.Web.Components;
using RobotCompetitionBooth.Web.Services;

var applicationBaseDirectory = Path.GetFullPath(AppContext.BaseDirectory)
    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
var runsFromExtractedSingleFile = executableDirectory is not null &&
    !string.Equals(
        Path.GetFullPath(executableDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        applicationBaseDirectory,
        StringComparison.OrdinalIgnoreCase);

var builder = runsFromExtractedSingleFile
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = applicationBaseDirectory,
        WebRootPath = Path.Combine(applicationBaseDirectory, "wwwroot")
    })
    : WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Remove disconnected editors promptly while allowing brief network interruptions to reconnect.
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(5);
    })
    .AddHubOptions(options =>
        options.MaximumReceiveMessageSize = DeviceProgramStore.MaximumWorkspaceFileLength + (64 * 1024));
builder.Services.Configure<EmbeddedMqttOptions>(
    builder.Configuration.GetSection(EmbeddedMqttOptions.SectionName));
builder.Services.Configure<RobotSerialOptions>(
    builder.Configuration.GetSection(RobotSerialOptions.SectionName));
builder.Services.AddSingleton<BluetoothDiscoveryService>();
builder.Services.AddSingleton<WifiCredentialStore>();
builder.Services.AddSingleton<WifiNetworkScanner>();
builder.Services.AddSingleton<MqttBrokerAccessService>();
builder.Services.AddSingleton<MqttBrokerEndpointProvider>();
builder.Services.AddSingleton<RobotDeviceStateService>();
builder.Services.AddSingleton<DeviceProgramStore>();
builder.Services.AddSingleton<WorkspaceCompiler>();
builder.Services.AddSingleton<RobotProgramDeploymentService>();
builder.Services.AddSingleton<RobotCollaborationService>();
builder.Services.AddScoped<AdminAccessService>();
builder.Services.AddSingleton<BluetoothConnectionManager>();
builder.Services.AddSingleton<EmbeddedMqttBrokerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<EmbeddedMqttBrokerService>());
builder.Services.AddSingleton<RobotSerialTerminalService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RobotSerialTerminalService>());
builder.Services.AddHostedService<BluetoothShutdownService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

var configuredUrls = (app.Configuration["urls"] ?? string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var hasHttpsEndpoint = configuredUrls.Any(url =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) ||
    app.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any(endpoint =>
    {
        var url = endpoint["Url"];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    });
if (hasHttpsEndpoint)
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
