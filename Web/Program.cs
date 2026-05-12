using Web.Configs;


// This is the main entry point for the ASP.NET Core web application.
var builder = WebApplication.CreateBuilder(args);

// Configure Services: Builder phase
builder.AddApplicationServices();

// Build the application.
var app = builder.Build();

// Configure Pipeline: App phase
await app.UseApplicationPipelineAsync();

// Run the application, starting the web server and listening for incoming HTTP requests.
app.Run();