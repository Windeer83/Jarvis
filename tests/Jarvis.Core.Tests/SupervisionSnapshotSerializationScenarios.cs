using System.Text.Json;
using Jarvis.Contracts;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class SupervisionSnapshotSerializationScenarios
{
    [Fact]
    public void Snapshot_wire_round_trip_keeps_active_templates_and_plans_without_legacy_duplicates()
    {
        var now = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        var commitmentId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var active = new ActiveSupervisionView(
            commitmentId,
            ActivityClassification.Related,
            IsIdle: false,
            DeviationReason: null,
            DeviationStartedAt: null,
            CountedDeviation: TimeSpan.Zero,
            RelatedStableSince: now,
            ReminderMarkerActive: false,
            ReturnIntentAt: null,
            PendingPrompt: null,
            ActiveRest: null,
            LastUnobservableStartedAt: null,
            LastUnobservableEndedAt: null,
            RecentCorrections: []);
        var reminders = new ReminderSettings(true, 5, 20, 20, 3);
        var rest = new RestSettings(10, 15);
        var template = new CommitmentTemplateView(
            templateId,
            "journal",
            CommitmentKind.Offline,
            30,
            "review",
            null,
            [],
            SupervisionMode.Passive,
            reminders,
            [],
            rest,
            now,
            now,
            IsArchived: false);
        var plan = new RecurrencePlanView(
            planId,
            templateId,
            new RecurrencePattern(
                RecurrenceKind.SelectedDates,
                SelectedDates: [DateOnly.FromDateTime(now.Date)]),
            [
                new RecurrenceOccurrenceView(
                    commitmentId,
                    DateOnly.FromDateTime(now.Date),
                    now,
                    now.AddMinutes(30),
                    RecurrenceOccurrenceStatus.Active)
            ],
            now);
        var snapshot = new SupervisionSnapshot(
            now,
            commitmentId,
            [],
            LatestActivity: null,
            LatestReminder: null,
            active,
            [template],
            [plan]);

        var json = JsonSerializer.Serialize(snapshot, CoreProtocol.Json);
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Single(names, name => name == "templates");
        Assert.Single(names, name => name == "recurrencePlans");
        Assert.DoesNotContain(names, name => name.Contains("OrNull", StringComparison.OrdinalIgnoreCase));

        var roundTrip = JsonSerializer.Deserialize<SupervisionSnapshot>(json, CoreProtocol.Json)!;
        Assert.Equal(commitmentId, roundTrip.ActiveSupervision!.CommitmentId);
        Assert.Equal(templateId, Assert.Single(roundTrip.Templates).Id);
        Assert.Equal(planId, Assert.Single(roundTrip.RecurrencePlans).Id);
    }
}
