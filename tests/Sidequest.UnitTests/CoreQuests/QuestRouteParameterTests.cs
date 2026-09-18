using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Components.Pages.Quests;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Checks request-query binding and explicit parameter transfer without invoking application services or browser interop.</summary>
public sealed class QuestRouteParameterTests : BunitContext
{
    /// <summary>List routes transfer exact query intent, including absence and invalid view names for downstream validation.</summary>
    /// <param name="view">Raw requested view, or null when absent.</param>
    /// <param name="filterEvent">Whether an Event filter is supplied.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Moderation", true)]
    [InlineData("History", false)]
    [InlineData("unexpected", true)]
    public void ListRoute_TransfersRequestQueryAsExplicitParameters(string? view, bool filterEvent)
    {
        ComponentFactories.AddStub<QuestList>();
        Guid? eventId = filterEvent ? Guid.NewGuid() : null;
        var query = new List<string>();
        if (view is not null)
            query.Add($"view={Uri.EscapeDataString(view)}");
        if (eventId is not null)
            query.Add($"eventId={eventId}");
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/quests" + (query.Count == 0 ? "" : $"?{string.Join("&", query)}"));

        var route = Render<QuestListRoute>();
        var parameters = route.FindComponent<Stub<QuestList>>().Instance.Parameters;

        Assert.Equal(view, parameters.Get(x => x.View));
        Assert.Equal(eventId, parameters.Get(x => x.EventId));
    }

    /// <summary>Omitted moderation defaults to ordinary access; explicit true and false are preserved alongside the route identifier.</summary>
    /// <param name="query">Optional moderation query value.</param>
    /// <param name="expected">Expected explicit moderation parameter.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void DetailsRoute_TransfersIdentityAndModerationIntent(string? query, bool expected)
    {
        ComponentFactories.AddStub<QuestDetails>();
        var id = Guid.NewGuid();
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/quests/{id}" + (query is null ? "" : $"?moderation={query}"));

        var route = Render<QuestDetailsRoute>(p => p.Add(x => x.Id, id));
        var parameters = route.FindComponent<Stub<QuestDetails>>().Instance.Parameters;

        Assert.Equal(id, parameters.Get(x => x.Id));
        Assert.Equal(expected, parameters.Get(x => x.Moderation));
    }

    /// <summary>Creation and existing editors preserve independent route identity and optional Event preselection.</summary>
    /// <param name="existing">Whether an existing Quest is being edited.</param>
    /// <param name="preselectEvent">Whether an Event was requested.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EditRoute_TransfersIdentityAndOptionalEvent(bool existing, bool preselectEvent)
    {
        ComponentFactories.AddStub<QuestEdit>();
        Guid? id = existing ? Guid.NewGuid() : null;
        Guid? eventId = preselectEvent ? Guid.NewGuid() : null;
        var path = existing ? $"/quests/{id}/edit" : "/quests/create";
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            path + (eventId is null ? "" : $"?eventId={eventId}"));

        var route = Render<QuestEditRoute>(p => p.Add(x => x.Id, id));
        var parameters = route.FindComponent<Stub<QuestEdit>>().Instance.Parameters;

        Assert.Equal(id, parameters.Get(x => x.Id));
        Assert.Equal(eventId, parameters.Get(x => x.EventId));
    }

    /// <summary>Request routes remain static and authorized while their views retain prerendered Server interactivity and direct parameters.</summary>
    /// <param name="route">Static request-binding component.</param>
    /// <param name="view">Corresponding interactive view.</param>
    [Theory]
    [InlineData(typeof(QuestListRoute), typeof(QuestList))]
    [InlineData(typeof(QuestDetailsRoute), typeof(QuestDetails))]
    [InlineData(typeof(QuestEditRoute), typeof(QuestEdit))]
    public void QueryBoundary_PreservesAuthorizationAndPrerendering(Type route, Type view)
    {
        Assert.NotNull(route.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(route.GetCustomAttribute<RenderModeAttribute>());
        Assert.NotEmpty(route.GetCustomAttributes<RouteAttribute>());
        Assert.Empty(view.GetCustomAttributes<RouteAttribute>());
        var mode = Assert.IsType<InteractiveServerRenderMode>(view.GetCustomAttribute<RenderModeAttribute>()?.Mode);
        Assert.True(mode.Prerender);
        foreach (var query in route.GetProperties().Where(p => p.IsDefined(typeof(SupplyParameterFromQueryAttribute))))
        {
            var input = Assert.IsAssignableFrom<PropertyInfo>(view.GetProperty(query.Name));
            Assert.NotNull(input.GetCustomAttribute<ParameterAttribute>());
            Assert.Null(input.GetCustomAttribute<SupplyParameterFromQueryAttribute>());
        }
    }
}
