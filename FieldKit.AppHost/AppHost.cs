var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

// PostgreSQL — the system of record. One database; each module owns a schema (ADR-0005).
// A persistent data volume keeps dev data across runs; pgweb gives a quick admin UI in dev.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgWeb();

var database = postgres.AddDatabase("fieldkitdb");

var server = builder.AddProject<Projects.FieldKit_Server>("server")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The Next.js app runs as its own process (Aspire assigns the port via PORT + generates a
// Dockerfile on publish), calling the API through service discovery. It is a standalone app, not
// static files served by the Server (ADR-0004).
builder.AddJavaScriptApp("webfrontend", "../frontend", "dev")
    .WithReference(server)
    .WaitFor(server)
    .WithExternalHttpEndpoints();

builder.Build().Run();
