using Web.Configs.Hosting;


// Create a WebApplication builder with the provided command-line arguments.
var builder = WebApplication.CreateBuilder(args);

// Add application services to the dependency injection container.
// Build the WebApplication instance from the configured builder.
var app = builder.AddApplicationServices().Build();

// Configure the HTTP request pipeline using the defined middleware components in the specified order.
// Initialize infrastructure components after configuring the pipeline.
await app.UseApplicationPipeline().InitializeInfrastructureAsync();

// Start the application and begin processing HTTP requests.
await app.RunAsync();
