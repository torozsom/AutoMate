using Web.Configs;


// Create a WebApplication builder with the provided command-line arguments.
var builder = WebApplication.CreateBuilder(args);

// Add application services to the dependency injection container.
builder.AddApplicationServices();

// Build the WebApplication instance from the configured builder.
var app = builder.Build();

// Configure the HTTP request pipeline using the defined middleware components in the specified order.
app.UseApplicationPipeline();

// Initialize infrastructure components.
await app.InitializeInfrastructureAsync();

// Start the application and begin processing HTTP requests.
await app.RunAsync();