using System.Collections.Concurrent;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class AgentBrowserSessionHostTests
{
    private const string DomainPolicyDeniedCode =
        "browser_domain_policy_denied";

    [Fact]
    public async Task Real_broker_authorizes_dispatches_and_audits_one_exact_browser_read()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentBrowserHostFixture.CreateAsync(
            authorizationConsumer: broker);
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default));
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.ReadState(fixture.SessionId));
        var requested =
            Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
                await broker.RequestAsync(action.Proposal, default));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    fixture.HumanContext().Actor,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));

        var state = Assert.IsType<AgentBrowserActionResult.State>(
            (await fixture.Client.RunAgentBrowserActionAsync(
                authorized.Authorization.Id,
                action,
                default)).Value());

        Assert.Equal(fixture.InitialAddress, state.Value.Address);
        var events = audit.Events
            .Where(item => item.CorrelationId == action.Proposal.Id.Value)
            .ToArray();
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            events.Select(item => item.Outcome));
        Assert.All(
            events,
            item => Assert.Equal(
                BuiltInAgentTools.BrowserReadState,
                item.Action));
        Assert.Equal(
            AgentAuthorizationSource.HumanApproval,
            Assert.IsType<AuditDetails.AgentActionDetails>(
                events[^1].Details).AuthorizationSource);
    }

    [Fact]
    public async Task Real_broker_auto_navigation_still_requires_exact_human_approval()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentBrowserHostFixture.CreateAsync(
            authorizationConsumer: broker);
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.BrowserNavigation,
                AgentPermission.Auto),
        };
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                policy,
                policyGeneration: 0),
            default));
        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://outside.example.test/"));
        var requested =
            Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
                await broker.RequestAsync(action.Proposal, default));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    fixture.HumanContext().Actor,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorized.Authorization.Id,
            action,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        var events = audit.Events
            .Where(item => item.CorrelationId == action.Proposal.Id.Value)
            .ToArray();
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            events.Select(item => item.Outcome));
        var completed = Assert.IsType<AuditDetails.AgentActionDetails>(
            events[^1].Details);
        Assert.Equal(
            AgentAuthorizationSource.HumanApproval,
            completed.AuthorizationSource);
        Assert.Equal("navigate_completed", completed.ResultCode);
    }

    // End-to-end execution contract for the closed browser tool family.

    [Fact]
    public async Task Agent_context_projects_and_refreshes_trusted_browser_document_identity()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();

        var initial = await fixture.InspectAsync();
        var initialPanel = Assert.Single(initial.Panels);
        var initialMetadata = Assert.IsType<BrowserSessionMetadata>(
            initialPanel.BrowserMetadata);
        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(fixture.Renderer.State.Address),
            initialMetadata.Origin);
        Assert.Equal(
            fixture.Renderer.State.DocumentRevision,
            initialMetadata.DocumentRevision);

        var destination = Address("https://other.example.test/committed");
        _ = (await fixture.Renderer.NavigateAsync(destination, default)).Value;
        var refreshed = await fixture.InspectAsync();
        var refreshedPanel = Assert.Single(refreshed.Panels);
        var refreshedMetadata = Assert.IsType<BrowserSessionMetadata>(
            refreshedPanel.BrowserMetadata);

        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(destination),
            refreshedMetadata.Origin);
        Assert.Equal(
            fixture.Renderer.State.DocumentRevision,
            refreshedMetadata.DocumentRevision);
        Assert.True(
            refreshedPanel.SessionRevision > initialPanel.SessionRevision);
        Assert.NotEqual(
            initial.BindingFingerprint,
            refreshed.BindingFingerprint);
    }

    [Fact]
    public async Task HumanApprovedClickBindsExactReferenceRevisionAndCurrentOriginOnce()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var sourceState = fixture.Renderer.State;
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                sourceState.DocumentRevision));
        Assert.Equal(
            BrowserNavigationOrigin
                .FromAddress(sourceState.Address)
                .CanonicalValue,
            Assert.Single(
                action.Proposal.Presentation.Arguments,
                argument => argument.Name == "origin").DisplayValue);

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Renderer.ClickCount);
        var reference = Assert.IsType<BrowserElementReference>(
            fixture.Renderer.LastClickedReference);
        Assert.Equal("snapshot_button-1", reference.Id.Value);
        Assert.Equal(sourceState.Address, reference.Document.Address);
        Assert.Equal(
            sourceState.DocumentRevision,
            reference.Document.DocumentRevision);
        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(sourceState.Address),
            fixture.Renderer.LastClickOrigin);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Succeeded,
            "click_completed");
    }

    [Fact]
    public async Task Browser_document_drift_after_preparation_fails_before_authorization_consumption()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));
        _ = (await fixture.Renderer.NavigateAsync(
            Address("https://other.example.test/changed"),
            default)).Value;

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.ClickCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task HumanApprovedFillBindsExactReferenceTextRevisionAndCurrentOriginOnce()
    {
        const string Text = "replacement value 😀";
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var sourceState = fixture.Renderer.State;
        var action = await fixture.PrepareAsync(
            Fill(
                fixture.SessionId,
                "snapshot_field-1",
                sourceState.DocumentRevision,
                Text));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Renderer.FillCount);
        var reference = Assert.IsType<BrowserElementReference>(
            fixture.Renderer.LastFilledReference);
        Assert.Equal("snapshot_field-1", reference.Id.Value);
        Assert.Equal(sourceState.Address, reference.Document.Address);
        Assert.Equal(
            sourceState.DocumentRevision,
            reference.Document.DocumentRevision);
        Assert.Equal(Text, fixture.Renderer.LastFillText);
        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(sourceState.Address),
            fixture.Renderer.LastFillOrigin);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Succeeded,
            "fill_completed");
    }

    [Fact]
    public async Task HumanApprovedCheckBindsExactReferenceRevisionAndCurrentOriginOnce()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var sourceState = fixture.Renderer.State;
        var action = await fixture.PrepareAsync(
            Check(
                fixture.SessionId,
                "snapshot_checkbox-1",
                sourceState.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Renderer.CheckCount);
        var reference = Assert.IsType<BrowserElementReference>(
            fixture.Renderer.LastCheckedReference);
        Assert.Equal("snapshot_checkbox-1", reference.Id.Value);
        Assert.Equal(sourceState.Address, reference.Document.Address);
        Assert.Equal(
            sourceState.DocumentRevision,
            reference.Document.DocumentRevision);
        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(sourceState.Address),
            fixture.Renderer.LastCheckOrigin);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Succeeded,
            "check_completed");
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.AutoPolicy)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task ClickRequiresExactHumanApprovalAtTheHostBoundary(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action, source: source),
            action,
            default);

        Assert.Equal(DomainPolicyDeniedCode, result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.ClickCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.AutoPolicy)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task FillRequiresExactHumanApprovalAtTheHostBoundary(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Fill(
                fixture.SessionId,
                "snapshot_field-1",
                fixture.Renderer.State.DocumentRevision,
                "value"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action, source: source),
            action,
            default);

        Assert.Equal(DomainPolicyDeniedCode, result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.FillCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.AutoPolicy)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task CheckRequiresExactHumanApprovalAtTheHostBoundary(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Check(
                fixture.SessionId,
                "snapshot_checkbox-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action, source: source),
            action,
            default);

        Assert.Equal(DomainPolicyDeniedCode, result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.CheckCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Fact]
    public async Task FillRequiresAReadyBrowserAndTheExactObservedRevision()
    {
        await using var loading = await AgentBrowserHostFixture.CreateAsync();
        loading.Renderer.BeginExternalLoad();
        var loadingAction = await loading.PrepareAsync(
            Fill(
                loading.SessionId,
                "snapshot_field-1",
                loading.Renderer.State.DocumentRevision,
                "value"));

        var loadingResult =
            await loading.Client.RunAgentBrowserActionAsync(
                loading.Authorization.Arm(loadingAction),
                loadingAction,
                default);

        Assert.Equal(
            "navigation_in_progress",
            loadingResult.Error().StableCode);
        Assert.True(loadingResult.Error().Retryable);
        Assert.Equal(0, loading.Renderer.FillCount);

        await using var stale = await AgentBrowserHostFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stale.PrepareAsync(
                    Fill(
                        stale.SessionId,
                        "snapshot_field-1",
                        stale.Renderer.State.DocumentRevision + 1,
                        "value"))
                .AsTask());
        Assert.Equal(0, stale.Renderer.FillCount);
        Assert.Equal(0, stale.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task ClickRequiresAReadyBrowserAndTheExactObservedRevision()
    {
        await using var loading = await AgentBrowserHostFixture.CreateAsync();
        loading.Renderer.BeginExternalLoad();
        var loadingAction = await loading.PrepareAsync(
            Click(
                loading.SessionId,
                "snapshot_button-1",
                loading.Renderer.State.DocumentRevision));

        var loadingResult =
            await loading.Client.RunAgentBrowserActionAsync(
                loading.Authorization.Arm(loadingAction),
                loadingAction,
                default);

        Assert.Equal(
            "navigation_in_progress",
            loadingResult.Error().StableCode);
        Assert.True(loadingResult.Error().Retryable);
        Assert.Equal(0, loading.Renderer.ClickCount);

        await using var stale = await AgentBrowserHostFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stale.PrepareAsync(
                    Click(
                        stale.SessionId,
                        "snapshot_button-1",
                        stale.Renderer.State.DocumentRevision + 1))
                .AsTask());
        Assert.Equal(0, stale.Renderer.ClickCount);
        Assert.Equal(0, stale.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task CheckRequiresAReadyBrowserAndTheExactObservedRevision()
    {
        await using var loading = await AgentBrowserHostFixture.CreateAsync();
        loading.Renderer.BeginExternalLoad();
        var loadingAction = await loading.PrepareAsync(
            Check(
                loading.SessionId,
                "snapshot_checkbox-1",
                loading.Renderer.State.DocumentRevision));

        var loadingResult =
            await loading.Client.RunAgentBrowserActionAsync(
                loading.Authorization.Arm(loadingAction),
                loadingAction,
                default);

        Assert.Equal(
            "navigation_in_progress",
            loadingResult.Error().StableCode);
        Assert.True(loadingResult.Error().Retryable);
        Assert.Equal(0, loading.Renderer.CheckCount);

        await using var stale = await AgentBrowserHostFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stale.PrepareAsync(
                    Check(
                        stale.SessionId,
                        "snapshot_checkbox-1",
                        stale.Renderer.State.DocumentRevision + 1))
                .AsTask());
        Assert.Equal(0, stale.Renderer.CheckCount);
        Assert.Equal(0, stale.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task ClickRechecksTheElementBindingAtTheRendererBoundary()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));
        fixture.Renderer.AdvanceDocumentBeforeBindingValidation = true;

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(
            HostErrorCode.InvalidRequest,
            result.Error().Code);
        Assert.Equal(
            "browser_element_reference_stale",
            result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.ClickCount);
    }

    [Fact]
    public async Task UnknownClickOutcomeIsAlwaysNonRetryable()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.InteractionOutcomeUnknown,
            "The native invocation did not return a conclusive outcome.",
            retryable: true);
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            "browser_interaction_outcome_unknown",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.ClickCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_interaction_outcome_unknown");
    }

    [Fact]
    public async Task NonInteractableClickIsRejectedWithItsStableCode()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.ElementNotInteractable,
            "The referenced element cannot be clicked.",
            retryable: false);
        var action = await fixture.PrepareAsync(
            Click(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(
            "browser_element_not_interactable",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.ClickCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_element_not_interactable");
    }

    [Fact]
    public async Task NonFillableElementIsRejectedWithItsStableCode()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.ElementNotFillable,
            "The referenced element cannot accept text.",
            retryable: true);
        var action = await fixture.PrepareAsync(
            Fill(
                fixture.SessionId,
                "snapshot_field-1",
                fixture.Renderer.State.DocumentRevision,
                "value"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(
            "browser_element_not_fillable",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.FillCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_element_not_fillable");
    }

    [Fact]
    public async Task NonCheckableElementIsRejectedWithItsStableCode()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.ElementNotCheckable,
            "The referenced element cannot be checked.",
            retryable: true);
        var action = await fixture.PrepareAsync(
            Check(
                fixture.SessionId,
                "snapshot_button-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(
            "browser_element_not_checkable",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.CheckCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_element_not_checkable");
    }

    [Fact]
    public async Task NormalizingFillValueIsRejectedWithItsStableCode()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.FillValueNotSupported,
            "The control would normalize the exact text.",
            retryable: true);
        var action = await fixture.PrepareAsync(
            Fill(
                fixture.SessionId,
                "snapshot_field-1",
                fixture.Renderer.State.DocumentRevision,
                "value"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(
            "browser_fill_value_not_supported",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.FillCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_fill_value_not_supported");
    }

    [Fact]
    public async Task AmbiguousFillFailureIsNonRetryableOutcomeUnknown()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.ExceptionMessage =
            "Renderer-private fill failure after dispatch.";
        var action = await fixture.PrepareAsync(
            Fill(
                fixture.SessionId,
                "snapshot_field-1",
                fixture.Renderer.State.DocumentRevision,
                "value"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            "browser_interaction_outcome_unknown",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.DoesNotContain(
            "Renderer-private",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Renderer.FillCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_interaction_outcome_unknown");
    }

    [Fact]
    public async Task AmbiguousCheckFailureIsNonRetryableOutcomeUnknown()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.ExceptionMessage =
            "Renderer-private check failure after dispatch.";
        var action = await fixture.PrepareAsync(
            Check(
                fixture.SessionId,
                "snapshot_checkbox-1",
                fixture.Renderer.State.DocumentRevision));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            "browser_interaction_outcome_unknown",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.DoesNotContain(
            "Renderer-private",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Renderer.CheckCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_interaction_outcome_unknown");
    }

    [Fact]
    public async Task State_read_and_all_navigation_operations_execute_and_complete_once()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();

        var read = await fixture.PrepareAsync(
            new AgentBrowserRequest.ReadState(fixture.SessionId));
        var readResult = Assert.IsType<AgentBrowserActionResult.State>(
            (await fixture.Client.RunAgentBrowserActionAsync(
                fixture.Authorization.Arm(read),
                read,
                default)).Value());

        Assert.Equal(fixture.InitialAddress, readResult.Value.Address);

        var snapshot = await fixture.PrepareAsync(
            new AgentBrowserRequest.Snapshot(fixture.SessionId));
        var snapshotResult =
            Assert.IsType<AgentBrowserActionResult.Snapshot>(
                (await fixture.Client.RunAgentBrowserActionAsync(
                    fixture.Authorization.Arm(snapshot),
                    snapshot,
                    default)).Value());
        Assert.Equal(
            fixture.InitialAddress,
            snapshotResult.Value.Document.Address);

        var destination = Address("https://docs.example.test/guide");
        AgentBrowserRequest[] requests =
        [
            new AgentBrowserRequest.Navigate(
                new BrowserNavigateRequest(fixture.SessionId, destination)),
            new AgentBrowserRequest.Back(fixture.SessionId),
            new AgentBrowserRequest.Forward(fixture.SessionId),
            new AgentBrowserRequest.Reload(fixture.SessionId),
            new AgentBrowserRequest.Stop(fixture.SessionId),
        ];
        foreach (var request in requests)
        {
            var action = await fixture.PrepareAsync(request);
            var result = await fixture.Client.RunAgentBrowserActionAsync(
                fixture.Authorization.Arm(action),
                action,
                default);

            Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        }

        Assert.Equal(destination, fixture.Renderer.State.Address);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        Assert.Equal(1, fixture.Renderer.BackCount);
        Assert.Equal(1, fixture.Renderer.ForwardCount);
        Assert.Equal(1, fixture.Renderer.ReloadCount);
        Assert.Equal(1, fixture.Renderer.StopCount);
        Assert.Equal(7, fixture.Authorization.ConsumeCount);
        Assert.Collection(
            fixture.Authorization.Completions,
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "state_read"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "snapshot_captured"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "navigate_completed"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "back_completed"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "forward_completed"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "reload_completed"),
            completion => AssertCompletion(
                completion,
                AgentActionOutcome.Succeeded,
                "stopped"));
    }

    [Fact]
    public async Task Auto_policy_allows_state_same_origin_navigation_reload_and_stop()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        AgentBrowserRequest[] requests =
        [
            new AgentBrowserRequest.ReadState(fixture.SessionId),
            new AgentBrowserRequest.Snapshot(fixture.SessionId),
            Navigate(
                fixture.SessionId,
                "https://INITIAL.example.test:443/next"),
            new AgentBrowserRequest.Reload(fixture.SessionId),
            new AgentBrowserRequest.Stop(fixture.SessionId),
        ];

        foreach (var request in requests)
        {
            var action = await fixture.PrepareAsync(request);
            var result = await fixture.Client.RunAgentBrowserActionAsync(
                fixture.Authorization.Arm(
                    action,
                    source: AgentAuthorizationSource.AutoPolicy),
                action,
                default);

            _ = result.Value();
        }

        Assert.Equal(1, fixture.Renderer.NavigateCount);
        Assert.Equal(1, fixture.Renderer.SnapshotCount);
        Assert.Equal(1, fixture.Renderer.ReloadCount);
        Assert.Equal(1, fixture.Renderer.StopCount);
        Assert.All(
            fixture.Authorization.Completions,
            completion => Assert.Equal(
                AgentActionOutcome.Succeeded,
                completion.Outcome));
    }

    [Theory]
    [InlineData("http://initial.example.test/")]
    [InlineData("https://other.example.test/")]
    [InlineData("https://initial.example.test:444/")]
    [InlineData("about:blank")]
    public async Task Auto_policy_denies_navigation_outside_the_current_origin(
        string destination)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, destination));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.AutoPolicy),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(
            DomainPolicyDeniedCode,
            result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Auto_policy_denies_history_with_an_unknown_destination(
        bool back)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BeginExternalLoad();
        AgentBrowserRequest request = back
            ? new AgentBrowserRequest.Back(fixture.SessionId)
            : new AgentBrowserRequest.Forward(fixture.SessionId);
        var action = await fixture.PrepareAsync(request);

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.AutoPolicy),
            action,
            default);

        Assert.Equal(
            DomainPolicyDeniedCode,
            result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.BackCount);
        Assert.Equal(0, fixture.Renderer.ForwardCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Fact]
    public async Task Loading_browser_rejects_a_new_governed_navigation()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BeginExternalLoad();
        var action = await fixture.PrepareAsync(
            Navigate(
                fixture.SessionId,
                "https://initial.example.test/next"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("navigation_in_progress", result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "navigation_in_progress");
    }

    [Fact]
    public async Task Loading_browser_rejects_snapshot_before_renderer_dispatch()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BeginExternalLoad();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Snapshot(fixture.SessionId));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.AutoPolicy),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("navigation_in_progress", result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        Assert.Equal(0, fixture.Renderer.SnapshotCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "navigation_in_progress");
    }

    [Fact]
    public async Task Snapshot_document_change_at_renderer_boundary_fails_closed()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.AdvanceDocumentBeforeBindingValidation = true;
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Snapshot(fixture.SessionId));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.AutoPolicy),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("browser_state_changed", result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        Assert.Equal(1, fixture.Renderer.SnapshotCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_state_changed");
    }

    [Fact]
    public async Task Document_change_at_renderer_boundary_rejects_stale_policy_binding()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.AdvanceDocumentBeforeBindingValidation = true;
        var action = await fixture.PrepareAsync(
            Navigate(
                fixture.SessionId,
                "https://approved.example.test/next"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("browser_state_changed", result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "browser_state_changed");
    }

    [Fact]
    public async Task Auto_policy_can_remain_on_about_blank_but_cannot_bootstrap_an_origin()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync(
            initialAddress: BrowserAddress.Blank);
        var remainBlank = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "about:blank"));

        var blankResult = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                remainBlank,
                source: AgentAuthorizationSource.AutoPolicy),
            remainBlank,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(blankResult.Value());
        var bootstrap = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://bootstrap.example.test/"));
        var bootstrapResult =
            await fixture.Client.RunAgentBrowserActionAsync(
                fixture.Authorization.Arm(
                    bootstrap,
                    source: AgentAuthorizationSource.AutoPolicy),
                bootstrap,
                default);

        Assert.Equal(
            DomainPolicyDeniedCode,
            bootstrapResult.Error().StableCode);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
    }

    [Fact]
    public async Task Yolo_policy_never_reaches_the_browser_renderer()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Reload(fixture.SessionId));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.YoloPolicy),
            action,
            default);

        Assert.Equal(
            DomainPolicyDeniedCode,
            result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.ReloadCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Fact]
    public async Task Yolo_policy_never_reaches_browser_snapshot()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Snapshot(fixture.SessionId));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(
                action,
                source: AgentAuthorizationSource.YoloPolicy),
            action,
            default);

        Assert.Equal(
            DomainPolicyDeniedCode,
            result.Error().StableCode);
        Assert.Equal(0, fixture.Renderer.SnapshotCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Fact]
    public async Task Uncomposed_bridge_fails_before_authorization_or_browser_dispatch()
    {
        await using (var missingComposer =
                     await AgentBrowserHostFixture.CreateAsync(
                         includeComposer: false))
        {
            var action = await missingComposer.PrepareAsync(
                new AgentBrowserRequest.Reload(missingComposer.SessionId));
            var result =
                await missingComposer.Client.RunAgentBrowserActionAsync(
                    missingComposer.Authorization.Arm(action),
                    action,
                    default);

            Assert.Equal(
                HostErrorCode.CapabilityNotSupported,
                result.Error().Code);
            Assert.Equal(0, missingComposer.Authorization.ConsumeCount);
            Assert.Equal(0, missingComposer.Renderer.ReloadCount);
            Assert.Empty(missingComposer.Authorization.Completions);
        }

        await using (var missingConsumer =
                     await AgentBrowserHostFixture.CreateAsync(
                         includeAuthorizationConsumer: false))
        {
            var action = await missingConsumer.PrepareAsync(
                new AgentBrowserRequest.Reload(missingConsumer.SessionId));
            var result =
                await missingConsumer.Client.RunAgentBrowserActionAsync(
                    missingConsumer.Authorization.Arm(action),
                    action,
                    default);

            Assert.Equal(
                HostErrorCode.CapabilityNotSupported,
                result.Error().Code);
            Assert.Equal(0, missingConsumer.Authorization.ConsumeCount);
            Assert.Equal(0, missingConsumer.Renderer.ReloadCount);
            Assert.Empty(missingConsumer.Authorization.Completions);
        }
    }

    // Prepared action, authorization, target, and attachment binding defenses.

    [Fact]
    public async Task Forged_request_and_proposal_pair_is_rejected_before_consumption()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var actionId = AgentActionId.New();
        var approved = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://approved.example.test/"),
            actionId);
        var changed = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://changed.example.test/"),
            actionId);
        var forged = new AgentBrowserAction(
            changed.Request,
            approved.Proposal);
        var authorizationId = fixture.Authorization.Arm(approved);

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            forged,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Wrong_authorization_and_changed_material_cannot_dispatch()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var actionId = AgentActionId.New();
        var approved = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://approved.example.test/"),
            actionId);
        var changed = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://changed.example.test/"),
            actionId);
        var authorizationId = fixture.Authorization.Arm(approved);

        var wrongAuthorization =
            await fixture.Client.RunAgentBrowserActionAsync(
                AgentAuthorizationId.New(),
                approved,
                default);
        var wrongMaterial =
            await fixture.Client.RunAgentBrowserActionAsync(
                authorizationId,
                changed,
                default);

        Assert.Equal(
            HostErrorCode.InvalidRequest,
            wrongAuthorization.Error().Code);
        Assert.Equal(HostErrorCode.InvalidRequest, wrongMaterial.Error().Code);
        Assert.Equal(2, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Replacing_the_exact_panel_session_rejects_the_stale_target()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://stale.example.test/"));
        var authorizationId = fixture.Authorization.Arm(action);
        var replacementId = new SessionId("browser-replacement");
        var replacementRenderer = new ControlledBrowserRenderer(
            Address("https://replacement.example.test/"));

        _ = await fixture.OpenActiveBrowserSessionAsync(
            replacementId,
            replacementRenderer,
            fixture.ClientId);
        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.NavigateCount);
        Assert.Equal(0, replacementRenderer.NavigateCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Exact_interactive_attachment_is_required_before_consumption()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync(
            attachInteractive: false);
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.ReadState(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Approving_client_must_own_the_exact_interactive_attachment()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Reload(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(
            action,
            approvingClientId: new ClientId("different-client"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.ReloadCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            "invalid_request");
    }

    [Fact]
    public async Task Attachment_revoked_during_authorization_consumption_fails_closed()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Reload(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsumes = true;

        var running = fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        _ = (await fixture.Client.DetachAsync(
            new DetachSessionRequest(
                fixture.Attachment!.Id,
                fixture.SessionId),
            fixture.HumanContext(),
            default)).Value();
        fixture.Authorization.ReleaseConsume.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("attachment_revoked", result.Error().StableCode);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.Renderer.ReloadCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Cancelled,
            "attachment_revoked");
    }

    // In-flight authority and engine-result semantics.

    [Theory]
    [InlineData(true, true, false, true, "authority_revoked")]
    [InlineData(false, true, false, true, "session_revoked")]
    [InlineData(false, false, true, true, "attachment_revoked")]
    [InlineData(false, false, false, true, "caller_cancelled")]
    public async Task Cancellation_reports_the_highest_precedence_stable_cause(
        bool revokePermit,
        bool revokeRuntime,
        bool revokeAttachment,
        bool cancelCaller,
        string expectedStableCode)
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BlockOperations = true;
        fixture.Renderer.CancellationMode =
            ControlledCancellationMode.ObserveAfterRelease;
        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://blocked.example.test/"));
        var authorizationId = fixture.Authorization.Arm(action);
        using var callerCancellation = new CancellationTokenSource();

        var running = fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            callerCancellation.Token).AsTask();
        await fixture.Renderer.OperationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        if (cancelCaller)
        {
            callerCancellation.Cancel();
        }

        if (revokeAttachment)
        {
            _ = (await fixture.Client.DetachAsync(
                new DetachSessionRequest(
                    fixture.Attachment!.Id,
                    fixture.SessionId),
                fixture.HumanContext(),
                default)).Value();
        }

        if (revokeRuntime)
        {
            _ = (await fixture.Client.CloseAsync(
                CloseScopeRequest.Panel(
                    fixture.PanelId,
                    CloseDecision.Request),
                fixture.HumanContext(),
                default)).Value();
        }

        if (revokePermit)
        {
            fixture.Authorization.PermitAuthority.Cancel();
        }

        fixture.Renderer.ReleaseOperation.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal(expectedStableCode, result.Error().StableCode);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Cancelled,
            expectedStableCode);
    }

    [Theory]
    [InlineData(false, "navigation_failed")]
    [InlineData(true, "engine_failed")]
    public async Task Browser_and_engine_failures_do_not_leak_renderer_messages(
        bool throwException,
        string expectedStableCode)
    {
        const string Secret = "renderer-secret: customer-internal.example";
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        if (throwException)
        {
            fixture.Renderer.ExceptionMessage = Secret;
        }
        else
        {
            fixture.Renderer.Failure = BrowserError.Create(
                BrowserErrorCode.NavigationFailed,
                Secret,
                retryable: true);
        }

        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://failure.example.test/"));
        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(expectedStableCode, result.Error().StableCode);
        Assert.DoesNotContain(
            Secret,
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            expectedStableCode);
    }

    [Fact]
    public async Task Redirect_policy_failure_is_preserved_and_audited_once()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.Failure = BrowserError.Create(
            BrowserErrorCode.NavigationPolicyDenied,
            "Renderer-private redirect detail.");
        var action = await fixture.PrepareAsync(
            Navigate(
                fixture.SessionId,
                "https://redirect.example.test/"));

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(DomainPolicyDeniedCode, result.Error().StableCode);
        Assert.DoesNotContain(
            "Renderer-private",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Failed,
            DomainPolicyDeniedCode);
    }

    [Fact]
    public async Task Successful_engine_effect_wins_late_caller_cancellation()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BlockOperations = true;
        fixture.Renderer.CancellationMode =
            ControlledCancellationMode.Ignore;
        var destination = Address("https://committed.example.test/");
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Navigate(
                new BrowserNavigateRequest(
                    fixture.SessionId,
                    destination)));
        var authorizationId = fixture.Authorization.Arm(action);
        using var callerCancellation = new CancellationTokenSource();

        var running = fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            callerCancellation.Token).AsTask();
        await fixture.Renderer.OperationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        callerCancellation.Cancel();
        fixture.Renderer.ReleaseOperation.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(destination, fixture.Renderer.State.Address);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        AssertCompletion(
            Assert.Single(fixture.Authorization.Completions),
            AgentActionOutcome.Succeeded,
            "navigate_completed");
        Assert.False(fixture.Authorization.LastCompletionTokenWasCancelled);
    }

    // Completion persistence and exactly-once dispatch behavior.

    [Fact]
    public async Task Transient_completion_audit_failure_retries_without_redispatch()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Reload(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.NextCompletionFailure =
            new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "The first completion audit attempt failed.");

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentBrowserActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Renderer.ReloadCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
    }

    [Fact]
    public async Task Completion_audit_failure_fails_closed_without_redispatch()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentBrowserRequest.Reload(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.CompletionFailure =
            new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "The completion audit is unavailable.");

        var result = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Renderer.ReloadCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
    }

    [Fact]
    public async Task Concurrent_authorization_reuse_dispatches_at_most_once()
    {
        await using var fixture = await AgentBrowserHostFixture.CreateAsync();
        fixture.Renderer.BlockOperations = true;
        var action = await fixture.PrepareAsync(
            Navigate(fixture.SessionId, "https://once.example.test/"));
        var authorizationId = fixture.Authorization.Arm(action);

        var first = fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Renderer.OperationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        var second = await fixture.Client.RunAgentBrowserActionAsync(
            authorizationId,
            action,
            default);
        fixture.Renderer.ReleaseOperation.TrySetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<AgentBrowserActionResult.Completed>(firstResult.Value());
        Assert.Equal(HostErrorCode.InvalidRequest, second.Error().Code);
        Assert.Equal(1, fixture.Renderer.NavigateCount);
        Assert.Equal(2, fixture.Authorization.ConsumeCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    private static void AssertCompletion(
        AgentActionCompletion completion,
        AgentActionOutcome outcome,
        string stableCode)
    {
        Assert.Equal(outcome, completion.Outcome);
        Assert.Equal(stableCode, completion.StableCode);
    }

    private static AgentBrowserRequest.Navigate Navigate(
        SessionId sessionId,
        string address) =>
        new(new BrowserNavigateRequest(sessionId, Address(address)));

    private static AgentBrowserRequest.Click Click(
        SessionId sessionId,
        string reference,
        long documentRevision) =>
        new(
            new BrowserElementClickRequest(
                sessionId,
                new BrowserElementReferenceId(reference),
                documentRevision));

    private static AgentBrowserRequest.Fill Fill(
        SessionId sessionId,
        string reference,
        long documentRevision,
        string text) =>
        new(
            new BrowserElementFillRequest(
                sessionId,
                new BrowserElementReferenceId(reference),
                documentRevision,
                text));

    private static AgentBrowserRequest.Check Check(
        SessionId sessionId,
        string reference,
        long documentRevision) =>
        new(
            new BrowserElementCheckRequest(
                sessionId,
                new BrowserElementReferenceId(reference),
                documentRevision));

    private static BrowserAddress Address(string value) =>
        new(new Uri(value, UriKind.Absolute));

    // A complete browser graph fixture keeps each test focused on one bridge
    // invariant while retaining real session-host attachment and graph logic.

    private sealed class AgentBrowserHostFixture : IAsyncDisposable
    {
        private AgentBrowserHostFixture(
            bool includeComposer,
            bool includeAuthorizationConsumer,
            BrowserAddress? initialAddress,
            IAgentAuthorizationConsumer? authorizationConsumer)
        {
            Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            BrowserFactory = new FakeBrowserPanelSessionFactory();
            Composer = new AgentBrowserActionComposer();
            Authorization = new FakeBrowserAuthorizationConsumer(Clock);
            InitialAddress = initialAddress
                ?? Address("https://initial.example.test/");
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                browserPanelFactory: BrowserFactory,
                agentBrowserActionComposer:
                    includeComposer ? Composer : null,
                agentAuthorizationConsumer:
                    includeAuthorizationConsumer
                        ? authorizationConsumer ?? Authorization
                        : null);
        }

        public ManualTimeProvider Clock { get; }

        public FakeBrowserPanelSessionFactory BrowserFactory { get; }

        public AgentBrowserActionComposer Composer { get; }

        public FakeBrowserAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("browser-client");

        public WindowInstanceId WindowId { get; } = new("window-1");

        public WorkspaceInstanceId WorkspaceId { get; } = new("workspace-1");

        public TabInstanceId TabId { get; } = new("tab-1");

        public PanelInstanceId PanelId { get; } = new("panel-1");

        public SessionId SessionId { get; } = new("browser-1");

        public AgentRunId RunId { get; } = new("run-1");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("agent-1"),
            ActorKind.Agent,
            "Test agent");

        public BrowserAddress InitialAddress { get; }

        public ControlledBrowserRenderer Renderer { get; private set; } = null!;

        public AttachmentPresence? Attachment { get; private set; }

        public static async ValueTask<AgentBrowserHostFixture> CreateAsync(
            bool includeComposer = true,
            bool includeAuthorizationConsumer = true,
            bool attachInteractive = true,
            BrowserAddress? initialAddress = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null)
        {
            var fixture = new AgentBrowserHostFixture(
                includeComposer,
                includeAuthorizationConsumer,
                initialAddress,
                authorizationConsumer);
            var panel = new PanelInstance(
                fixture.PanelId,
                PanelKind.Browser,
                "Browser");
            var tab = new TabInstance(
                fixture.TabId,
                "Primary",
                [panel],
                panel.Id);
            var workspace = new WorkspaceInstance(
                fixture.WorkspaceId,
                "Workspace",
                [tab],
                tab.Id);
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    workspace),
                fixture.HumanContext(),
                default)).Value();
            fixture.Renderer = new ControlledBrowserRenderer(
                fixture.InitialAddress);
            _ = await fixture.OpenActiveBrowserSessionAsync(
                fixture.SessionId,
                fixture.Renderer,
                fixture.ClientId,
                attachInteractive);
            return fixture;
        }

        public async ValueTask<AttachmentPresence?>
            OpenActiveBrowserSessionAsync(
                SessionId sessionId,
                ControlledBrowserRenderer renderer,
                ClientId clientId,
                bool attachInteractive = true)
        {
            _ = (await Client.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    new SessionOwner(
                        HostMode.Desktop,
                        WindowId,
                        WorkspaceId,
                        TabId,
                        PanelId),
                    "test browser",
                    renderer.State.Address),
                HumanContext(),
                default)).Value();
            if (!attachInteractive)
            {
                return null;
            }

            var attachment = (await Client.AttachAsync(
                new AttachSessionRequest(
                    sessionId,
                    clientId,
                    AttachmentKind.Interactive,
                    new ViewportDescriptor(1_024, 768, 1),
                    BrowserCapabilities()),
                HumanContext(clientId),
                default)).Value().Attachment;
            _ = (await Client.AttachBrowserRendererAsync(
                new AttachBrowserRendererRequest(
                    sessionId,
                    attachment.Id,
                    renderer),
                HumanContext(clientId),
                default)).Value();
            Attachment = attachment;
            return attachment;
        }

        public async ValueTask<AgentBrowserAction> PrepareAsync(
            AgentBrowserRequest request,
            AgentActionId? actionId = null)
        {
            var context = await InspectAsync();
            var now = Clock.GetUtcNow();
            return Composer.Prepare(
                new AgentActionEnvelope(
                    actionId ?? AgentActionId.New(),
                    RunId,
                    Agent,
                    policyGeneration: 0,
                    now,
                    now.AddMinutes(1)),
                context,
                request);
        }

        public async ValueTask<AgentContextSnapshot> InspectAsync() =>
            (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Panel(
                        WindowId,
                        WorkspaceId,
                        TabId,
                        PanelId)),
                AgentContext(),
                default)).Value();

        public OperationContext HumanContext(ClientId? clientId = null)
        {
            var authenticatedClientId = clientId ?? ClientId;
            return new OperationContext(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(authenticatedClientId.Value),
                    ActorKind.Human,
                    "Test user",
                    authenticatedClientId),
                CancellationId: CancellationId.New());
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        private static CapabilitySet BrowserCapabilities() => new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.AttachInteractive,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
        ]);
    }

    private sealed class InMemoryAuditStore : IAuditStore
    {
        private readonly object _gate = new();
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                        _events
                            .Where(item =>
                                item.CorrelationId == correlationId)
                            .ToArray()));
            }
        }
    }

    private sealed class FakeBrowserAuthorizationConsumer(
        TimeProvider timeProvider) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentBrowserAction? _authorizedAction;
        private AgentAuthorizationId _authorizationId;
        private ClientId _approvingClientId = new("browser-client");
        private int _consumed;
        private int _consumeCount;

        public int ConsumeCount => Volatile.Read(ref _consumeCount);

        public AgentAuthorizationError? CompletionFailure { get; set; }

        public AgentAuthorizationError? NextCompletionFailure { get; set; }

        public bool BlockConsumes { get; set; }

        public TaskCompletionSource ConsumeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseConsume { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource PermitAuthority { get; private set; } =
            new();

        public bool LastCompletionTokenWasCancelled { get; private set; }

        public IReadOnlyList<AgentActionCompletion> Completions =>
            _completions.ToArray();

        public AgentAuthorizationId Arm(
            AgentBrowserAction action,
            ClientId? approvingClientId = null,
            AgentAuthorizationSource source =
                AgentAuthorizationSource.HumanApproval)
        {
            ArgumentNullException.ThrowIfNull(action);
            _authorizedAction = action;
            _authorizationId = AgentAuthorizationId.New();
            _approvingClientId =
                approvingClientId ?? new ClientId("browser-client");
            AuthorizationSource = source;
            PermitAuthority = new CancellationTokenSource();
            Volatile.Write(ref _consumed, 0);
            CompletionFailure = null;
            NextCompletionFailure = null;
            return _authorizationId;
        }

        private AgentAuthorizationSource AuthorizationSource { get; set; } =
            AgentAuthorizationSource.HumanApproval;

        public async ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _consumeCount);
            ConsumeStarted.TrySetResult();
            if (BlockConsumes)
            {
                await ReleaseConsume.Task.WaitAsync(cancellationToken);
            }

            var action = _authorizedAction
                ?? throw new InvalidOperationException(
                    "An action must be armed before consuming authorization.");
            var expected = AgentActionExecutionBinding.FromProposal(
                action.Proposal);
            if (authorizationId != _authorizationId
                || !Matches(expected, currentBinding))
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationMismatch,
                    "The execution binding differs from the approved action.");
            }

            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationNotFound,
                    "The one-action authorization has already been consumed.");
            }

            if (!BuiltInAgentTools.Catalog.TryGet(
                    action.Proposal.ToolName,
                    out var tool))
            {
                throw new InvalidOperationException(
                    "The prepared action is missing from the built-in catalog.");
            }

            var now = timeProvider.GetUtcNow();
            var authorization = new AgentActionAuthorization(
                authorizationId,
                action.Proposal,
                tool!,
                AuthorizationSource,
                _approvingClientId,
                now.AddMinutes(1));
            return new AgentPermitResult.Granted(
                new AgentActionPermit(
                    authorization,
                    now,
                    PermitAuthority.Token));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(permit);
            ArgumentNullException.ThrowIfNull(completion);
            LastCompletionTokenWasCancelled =
                cancellationToken.IsCancellationRequested;
            _completions.Enqueue(completion);
            var nextFailure = NextCompletionFailure;
            NextCompletionFailure = null;
            return ValueTask.FromResult(
                nextFailure ?? CompletionFailure);
        }

        private static AgentPermitResult.Denied Denied(
            AgentAuthorizationErrorCode code,
            string message) =>
            new(new AgentAuthorizationError(code, message));

        private static bool Matches(
            AgentActionExecutionBinding expected,
            AgentActionExecutionBinding actual) =>
            expected.ActionId == actual.ActionId
            && expected.RunId == actual.RunId
            && expected.ActorId == actual.ActorId
            && string.Equals(
                expected.ToolName,
                actual.ToolName,
                StringComparison.Ordinal)
            && expected.Target == actual.Target
            && expected.TargetIdentity == actual.TargetIdentity
            && expected.TargetFingerprint == actual.TargetFingerprint
            && expected.ArgumentDigest == actual.ArgumentDigest
            && expected.PolicyGeneration == actual.PolicyGeneration;
    }

    // The renderer exposes deterministic synchronization points for authority
    // races without weakening the shared browser test fake used elsewhere.

    private enum ControlledCancellationMode
    {
        ObserveWhileBlocked,
        ObserveAfterRelease,
        Ignore,
    }

    private sealed class ControlledBrowserRenderer(
        BrowserAddress initialAddress) : IBrowserRenderer
    {
        public CapabilitySet Capabilities { get; } = new(
        [
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
        ]);

        public BrowserSessionState State { get; private set; } =
            BrowserSessionState.Initial(initialAddress);

        public int NavigateCount { get; private set; }

        public int BackCount { get; private set; }

        public int ForwardCount { get; private set; }

        public int ReloadCount { get; private set; }

        public int StopCount { get; private set; }

        public int SnapshotCount { get; private set; }

        public int ClickCount { get; private set; }

        public BrowserElementReference? LastClickedReference { get; private set; }

        public BrowserNavigationOrigin? LastClickOrigin { get; private set; }

        public int FillCount { get; private set; }

        public BrowserElementReference? LastFilledReference { get; private set; }

        public string? LastFillText { get; private set; }

        public BrowserNavigationOrigin? LastFillOrigin { get; private set; }

        public int CheckCount { get; private set; }

        public BrowserElementReference? LastCheckedReference { get; private set; }

        public BrowserNavigationOrigin? LastCheckOrigin { get; private set; }

        public bool BlockOperations { get; set; }

        public bool AdvanceDocumentBeforeBindingValidation { get; set; }

        public ControlledCancellationMode CancellationMode { get; set; }

        public BrowserError? Failure { get; set; }

        public string? ExceptionMessage { get; set; }

        public TaskCompletionSource OperationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseOperation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

        public void BeginExternalLoad()
        {
            State = new BrowserSessionState(
                State.Address,
                State.Title,
                BrowserLoadState.Loading,
                State.CanGoBack,
                State.CanGoForward,
                State.DocumentRevision);
            StateChanged?.Invoke(
                this,
                new BrowserStateChangedEventArgs(State));
        }

        public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
            BrowserAddress address,
            CancellationToken cancellationToken)
        {
            NavigateCount++;
            return RunAsync(address, cancellationToken);
        }

        public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
            CancellationToken cancellationToken)
        {
            BackCount++;
            return RunAsync(address: null, cancellationToken);
        }

        public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
            CancellationToken cancellationToken)
        {
            ForwardCount++;
            return RunAsync(address: null, cancellationToken);
        }

        public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
            CancellationToken cancellationToken)
        {
            ReloadCount++;
            return RunAsync(address: null, cancellationToken);
        }

        public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
            CancellationToken cancellationToken)
        {
            StopCount++;
            return RunAsync(address: null, cancellationToken);
        }

        public ValueTask<BrowserResult<BrowserSessionState>>
            NavigateWithinOriginAsync(
                BrowserOriginConstrainedNavigationRequest request,
                BrowserNavigationOrigin allowedOrigin,
                BrowserNavigationStartBinding startBinding,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(allowedOrigin);
            ArgumentNullException.ThrowIfNull(startBinding);
            if (AdvanceDocumentBeforeBindingValidation)
            {
                AdvanceDocumentBeforeBindingValidation = false;
                State = new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    State.CanGoBack,
                    State.CanGoForward,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            if (!startBinding.Matches(State))
            {
                return ValueTask.FromResult(
                    BrowserResult<BrowserSessionState>.Failure(
                        BrowserError.Create(
                            BrowserErrorCode.NavigationStateChanged,
                            "The browser document changed after authorization.",
                            retryable: true)));
            }

            if (!allowedOrigin.Allows(
                    request is BrowserOriginConstrainedNavigationRequest.Navigate
                        requestedNavigation
                        ? requestedNavigation.Address
                        : State.Address))
            {
                return ValueTask.FromResult(
                    BrowserResult<BrowserSessionState>.Failure(
                        BrowserError.Create(
                            BrowserErrorCode.NavigationPolicyDenied,
                            "The browser blocked a top-level navigation outside the approved origin.")));
            }

            return request switch
            {
                BrowserOriginConstrainedNavigationRequest.Navigate
                    navigationRequest =>
                    NavigateAsync(
                        navigationRequest.Address,
                        cancellationToken),
                BrowserOriginConstrainedNavigationRequest.Back =>
                    GoBackAsync(cancellationToken),
                BrowserOriginConstrainedNavigationRequest.Forward =>
                    GoForwardAsync(cancellationToken),
                BrowserOriginConstrainedNavigationRequest.Reload =>
                    ReloadAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
        }

        public async ValueTask<BrowserResult<BrowserDocumentSnapshot>>
            CaptureSnapshotAsync(
                BrowserDocumentBinding document,
                CancellationToken cancellationToken)
        {
            SnapshotCount++;
            if (AdvanceDocumentBeforeBindingValidation)
            {
                AdvanceDocumentBeforeBindingValidation = false;
                State = new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    State.CanGoBack,
                    State.CanGoForward,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            if (!document.Matches(State))
            {
                return BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser document changed during capture.",
                        retryable: true));
            }

            OperationStarted.TrySetResult();
            if (BlockOperations)
            {
                if (CancellationMode
                    == ControlledCancellationMode.ObserveWhileBlocked)
                {
                    await ReleaseOperation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ReleaseOperation.Task;
                }
            }

            if (CancellationMode != ControlledCancellationMode.Ignore)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ExceptionMessage is { } exceptionMessage)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            if (Failure is { } failure)
            {
                return BrowserResult<BrowserDocumentSnapshot>.Failure(
                    failure);
            }

            return BrowserResult<BrowserDocumentSnapshot>.Success(
                new BrowserDocumentSnapshot(
                    document,
                    [new BrowserSnapshotNode(
                        0,
                        "document",
                        "Example")],
                    DateTimeOffset.UnixEpoch));
        }

        public async ValueTask<BrowserResult<BrowserClickReceipt>>
            ClickWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(allowedOrigin);
            ClickCount++;
            LastClickedReference = reference;
            LastClickOrigin = allowedOrigin;
            if (AdvanceDocumentBeforeBindingValidation)
            {
                AdvanceDocumentBeforeBindingValidation = false;
                State = new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    State.CanGoBack,
                    State.CanGoForward,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            if (!reference.Document.Matches(State))
            {
                return BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true));
            }

            if (!allowedOrigin.Allows(State.Address))
            {
                return BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser click origin is no longer allowed."));
            }

            OperationStarted.TrySetResult();
            if (BlockOperations)
            {
                if (CancellationMode
                    == ControlledCancellationMode.ObserveWhileBlocked)
                {
                    await ReleaseOperation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ReleaseOperation.Task;
                }
            }

            if (CancellationMode != ControlledCancellationMode.Ignore)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ExceptionMessage is { } exceptionMessage)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            if (Failure is { } failure)
            {
                return BrowserResult<BrowserClickReceipt>.Failure(failure);
            }

            return BrowserResult<BrowserClickReceipt>.Success(
                new BrowserClickReceipt(reference.Document));
        }

        public async ValueTask<BrowserResult<BrowserFillReceipt>>
            FillWithinOriginAsync(
                BrowserElementReference reference,
                string text,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(allowedOrigin);
            FillCount++;
            LastFilledReference = reference;
            LastFillText = text;
            LastFillOrigin = allowedOrigin;
            if (AdvanceDocumentBeforeBindingValidation)
            {
                AdvanceDocumentBeforeBindingValidation = false;
                State = new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    State.CanGoBack,
                    State.CanGoForward,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            if (!reference.Document.Matches(State))
            {
                return BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true));
            }

            if (!allowedOrigin.Allows(State.Address))
            {
                return BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser fill origin is no longer allowed."));
            }

            OperationStarted.TrySetResult();
            if (BlockOperations)
            {
                if (CancellationMode
                    == ControlledCancellationMode.ObserveWhileBlocked)
                {
                    await ReleaseOperation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ReleaseOperation.Task;
                }
            }

            if (CancellationMode != ControlledCancellationMode.Ignore)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ExceptionMessage is { } exceptionMessage)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            if (Failure is { } failure)
            {
                return BrowserResult<BrowserFillReceipt>.Failure(failure);
            }

            return BrowserResult<BrowserFillReceipt>.Success(
                new BrowserFillReceipt(reference.Document));
        }

        public async ValueTask<BrowserResult<BrowserCheckReceipt>>
            CheckWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(allowedOrigin);
            CheckCount++;
            LastCheckedReference = reference;
            LastCheckOrigin = allowedOrigin;
            if (AdvanceDocumentBeforeBindingValidation)
            {
                AdvanceDocumentBeforeBindingValidation = false;
                State = new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    State.CanGoBack,
                    State.CanGoForward,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            if (!reference.Document.Matches(State))
            {
                return BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true));
            }

            if (!allowedOrigin.Allows(State.Address))
            {
                return BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser check origin is no longer allowed."));
            }

            OperationStarted.TrySetResult();
            if (BlockOperations)
            {
                if (CancellationMode
                    == ControlledCancellationMode.ObserveWhileBlocked)
                {
                    await ReleaseOperation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ReleaseOperation.Task;
                }
            }

            if (CancellationMode != ControlledCancellationMode.Ignore)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ExceptionMessage is { } exceptionMessage)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            if (Failure is { } failure)
            {
                return BrowserResult<BrowserCheckReceipt>.Failure(failure);
            }

            return BrowserResult<BrowserCheckReceipt>.Success(
                new BrowserCheckReceipt(reference.Document));
        }

        private async ValueTask<BrowserResult<BrowserSessionState>> RunAsync(
            BrowserAddress? address,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStarted.TrySetResult();
            if (BlockOperations)
            {
                if (CancellationMode
                    == ControlledCancellationMode.ObserveWhileBlocked)
                {
                    await ReleaseOperation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ReleaseOperation.Task;
                }
            }

            if (CancellationMode != ControlledCancellationMode.Ignore)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ExceptionMessage is { } exceptionMessage)
            {
                throw new InvalidOperationException(exceptionMessage);
            }

            if (Failure is { } failure)
            {
                return BrowserResult<BrowserSessionState>.Failure(failure);
            }

            if (address is not null)
            {
                State = new BrowserSessionState(
                    address,
                    address.Value.Host,
                    BrowserLoadState.Ready,
                    canGoBack: true,
                    canGoForward: false,
                    State.DocumentRevision + 1);
                StateChanged?.Invoke(
                    this,
                    new BrowserStateChangedEventArgs(State));
            }

            return BrowserResult<BrowserSessionState>.Success(State);
        }
    }
}
