using Microsoft.AspNetCore.Components;
using Sidequest.Application.Administration;

namespace Sidequest.Web.Components.Pages.Administration;

/// <summary>Confirmed business-setting edits with explicit conflict comparison and preserved unsaved text.</summary>
public partial class BusinessEmail
{
    [Inject] private BusinessEmailService Service { get; set; } = default!;
    private IReadOnlyList<BusinessSetting>? settings;
    private IReadOnlyList<BusinessSetting>? currentSettings;
    private string brand = "";
    private string replyTo = "";
    private string? pendingKey;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => RunAsync(async () =>
    {
        settings = await Service.GetSettingsAsync(Lifetime, allowInvalidForEditing: true);
        brand = settings.Single(x => x.Key == BusinessEmailRules.BrandKey).Value;
        replyTo = settings.Single(x => x.Key == BusinessEmailRules.ReplyToKey).Value;
        ValidateSettings(settings);
    });

    private Task RefreshAsync() => RunAsync(async () =>
    {
        var latest = await Service.GetSettingsAsync(Lifetime, allowInvalidForEditing: true);
        if (settings is null)
        {
            settings = latest;
            brand = latest.Single(x => x.Key == BusinessEmailRules.BrandKey).Value;
            replyTo = latest.Single(x => x.Key == BusinessEmailRules.ReplyToKey).Value;
        }
        else
        {
            currentSettings = latest;
        }
        ValidateSettings(latest);
    });

    private void AcceptVersions()
    {
        settings = currentSettings;
        currentSettings = null;
        pendingKey = null;
    }

    private Task SaveAsync() => RunAsync(async () =>
    {
        if (pendingKey is null || settings is null) return;
        var original = settings.Single(x => x.Key == pendingKey);
        await Service.SaveSettingAsync(original with { Value = pendingKey == BusinessEmailRules.BrandKey ? brand : replyTo }, Lifetime);
        settings = await Service.GetSettingsAsync(Lifetime, allowInvalidForEditing: true);
        pendingKey = null;
        currentSettings = null;
        Status = "Business email setting saved and audited. No provider configuration or mandatory policy changed.";
    });

    private static void ValidateSettings(IReadOnlyList<BusinessSetting> values)
    {
        foreach (var value in values)
            BusinessEmailRules.ValidateSetting(value.Key, value.Value);
    }

    /// <inheritdoc />
    protected override void ClearSensitiveState()
    {
        settings = null;
        currentSettings = null;
        pendingKey = null;
    }
}
