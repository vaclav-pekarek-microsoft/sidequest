using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.Web.Operations;

/// <summary>Composes notification operations and durable delivery dependencies without starting background processing.</summary>
public static class DeliveryRegistration
{
    /// <summary>Registers real notification, calendar, email and queue implementations using validated deployment settings.</summary>
    /// <param name="services">Host services with application policies, identity, persistence, logging and clock already registered.</param>
    /// <param name="configuration">Merged configuration containing optional Delivery:Email and Delivery:Work sections.</param>
    /// <returns>The same collection for subsequent feature and worker composition.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A worker duration or concurrency setting is outside its supported bounds.</exception>
    /// <exception cref="InvalidOperationException">A setting cannot be bound or the email submission timeout is invalid.</exception>
    /// <remarks>
    /// Call once at startup. Missing provider credentials remain explicit delivery failures rather than startup network calls.
    /// Register all feature handlers before separately activating DurableWorkHostedService; this method starts no worker.
    /// </remarks>
    public static IServiceCollection AddSidequestDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var email = new EmailDeliveryOptions();
        configuration.GetSection("Delivery:Email").Bind(email);
        var work = new DurableWorkOptions();
        configuration.GetSection("Delivery:Work").Bind(work);
        work.Validate();
        if (email.SubmissionTimeout <= TimeSpan.Zero || email.SubmissionTimeout > TimeSpan.FromSeconds(90))
            throw new InvalidOperationException("Delivery:Email:SubmissionTimeout must be positive and at most ninety seconds.");

        services.AddSingleton(email);
        services.AddSingleton(work);
        services.AddSingleton<IRecipientCalendarRenderer, RecipientCalendarRenderer>();
        services.AddSingleton<IEmailGateway, AcsEmailGateway>();
        services.AddScoped<RecipientPolicy>();
        services.AddScoped<ReminderScheduler>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<WorkExecutionContext>();
        services.AddScoped<SqlWorkQueue>();
        services.AddScoped<DeliveryDispatcher>();
        services.AddScoped<DurableWorkRunner>();
        services.AddScoped<IBackgroundWorkHandler, ChangeOutboxHandler>();
        services.AddScoped<IBackgroundWorkHandler, ReminderWorkHandler>();
        return services;
    }
}
