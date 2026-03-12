using Web;


// This is the main entry point for the ASP.NET Core web application.
var builder = WebApplication.CreateBuilder(args);

// Configure Services: Builder phase
ServiceConfiguration.Configure(builder);

// Build the application.
var app = builder.Build();

// Configure Pipeline: App phase
AppConfiguration.Configure(app);

// Run the application, starting the web server and listening for incoming HTTP requests.
app.Run();