using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Pixelz.API.Middleware;
using Pixelz.Application;
using Pixelz.Infrastructure;
using Pixelz.Infrastructure.Logging;
using Pixelz.Infrastructure.Persistence;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();
Log.Information("Pixelz API starting...");

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UsePixelzSerilog();
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
    var jwtSection = builder.Configuration.GetSection("Jwt");
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opt =>
        {
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSection["Issuer"],
                ValidAudience = jwtSection["Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Secret"] ?? "dev-secret-key-must-be-32-chars!!"))
            };
        });
    builder.Services.AddAuthorization();

    // ── Controllers & Swagger ─────────────────────────────────────────────────

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Pixelz Checkout API",
            Version = "v1",
            Description = "Order search and checkout for Pixelz retouching studio platform"
        });

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste your JWT token here"
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme{Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }},
                Array.Empty<string>()
            }
        });
    });

    builder.Services.AddHealthChecks().AddDbContextCheck<PixelzDbContext>("database");
    builder.Services.AddResponseCompression(opt => opt.EnableForHttps = true);
    var app = builder.Build();

    // ── Database initialisation ───────────────────────────────────────────────
    // Chạy mọi môi trường (Development, Staging, Production).
    // - Có migration files  → MigrateAsync()     (áp dụng migration còn thiếu)
    // - Chưa có migration   → EnsureCreatedAsync() (tạo schema thẳng từ DbContext)
    // Idempotent: bảng đã tồn tại sẽ bị bỏ qua, không gây lỗi.

    await DatabaseInitializer.InitialiseAsync(app.Services);

    // ── Middleware pipeline ───────────────────────────────────────────────────

    app.UseResponseCompression();
    app.UseSerilogRequestLogging(opt =>
    {
        opt.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";

        opt.EnrichDiagnosticContext = (diag, ctx) =>
        {
            diag.Set("UserAgent", ctx.Request.Headers.UserAgent.ToString());
            diag.Set("ClientIP", ctx.Connection.RemoteIpAddress?.ToString());
            diag.Set("RequestHost", ctx.Request.Host.Value);
        };
    });
    app.UseMiddleware<GlobalExceptionMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Pixelz API v1");
            c.DisplayRequestDuration();
        });
    }
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/api/v1/health");

    Log.Information("Pixelz API started. Environment={Env} Urls={Urls}", app.Environment.EnvironmentName, string.Join(", ", app.Urls));

    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}