using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components;
using Sidequest.Web.Operations;

var builder = WebApplication.CreateBuilder(args);
var authentication = FoundationAuthenticationSettings.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(authentication);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, WorkforceAuthenticationStateProvider>();
builder.Services.AddScoped<ICurrentUser, CircuitCurrentUser>();
builder.Services.AddScoped<WorkforceAccounts>();
builder.Services.AddSidequestApplication();
builder.Services.AddSidequestInfrastructure(builder.Configuration);
builder.Services.AddSidequestDelivery(builder.Configuration);
builder.Services.AddSidequestMedia(builder.Configuration);
builder.Services.AddSidequestExperience();
builder.Services.AddSidequestAdministration(builder.Configuration);
builder.Services.AddSingleton(new WorkHandlerRequirements(RequireMediaCleanup: true));
builder.Services.AddSidequestCoreWorkflows(builder.Configuration, authentication);
builder.Services.AddFoundationAuthentication(authentication, builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<SqlReadinessCheck>("sql", tags: ["ready"]);

var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    if (authentication.IsDevelopment && !AuthenticationEndpoints.IsLoopback(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    // No authenticated HTML or auth responses belong in shared/offline caches.
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapFoundationAuthentication(authentication);
app.MapSidequestNotifications();
app.MapSidequestMedia();
app.MapSidequestExperience();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

/// <summary>Exposes the web application entry point to integration-test hosts.</summary>
public partial class Program;
