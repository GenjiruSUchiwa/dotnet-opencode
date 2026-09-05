namespace OpenCode.Cli.Tui;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Cli.Tui.Components;
using OpenCode.Cli.Tui.Sessions;
using OpenCode.Cli.Tui.Permissions;
using OpenCode.Cli.Tui.Forms;
using OpenCode.Cli.Tui.Layout;
using OpenCode.Cli.Tui.Theme;
using OpenCode.Cli.Tui.Settings;
using OpenCode.Cli.Tui.MessageActions;
using OpenCode.Cli.Tui.Commands;
using OpenCode.Cli.Tui.Images;
using OpenCode.Cli.Tui.Skills;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Cli.Tui.Activities;
using OpenCode.Cli.Tui.Models;
using OpenCode.Cli.Tui.Recovery;
using OpenTui.Blazor.Nodes;
using OpenTui.Blazor.TextMarks;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Clipboard;
using OpenTui.Blazor.Code;

public static class InteractiveTui
{
    public static async Task RunAsync(ServiceEndpoint? explicitServer = null, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var directory = Directory.GetCurrentDirectory();
        var homeLocation = new LocationRef(directory);
        var themeCatalog = new ThemeCatalog();
        var settingsStore = new CliSettingsStore();
        var settings = new CliSettingsController(settingsStore);
        var themeSettings = CliThemeSettings.FromConfig(await settingsStore.ReadAsync());
        themeCatalog.SetCustom(await ThemeDiscovery.DiscoverAsync(ThemeDiscovery.ConfigDirectories(CliThemeSettings.GlobalConfigDirectory(), directory)));
        using var themes = new ThemeState(themeCatalog, themeSettings);
        themes.MarkReady();
        var keybindings = await ClientKeybindSettings.LoadAsync(CancellationToken.None);
        var modelPreferences = new ModelPreferenceService(clock: clock);
        string? modelPreferencesError = null;
        try { await modelPreferences.LoadAsync(); }
        catch (Exception exception) { modelPreferencesError = "Could not load model preferences: " + exception.Message; }
        var tabs = new SessionTabStorage(directory, clock: clock);
        ServiceEndpoint? endpoint = null;
        ServerReadinessClient? readiness = null;
        SessionClientAdapter? adapter = null;
        SessionHttpClient? api = null;
        AppCatalog? catalog = null;
        LocationRef? catalogLocation = null;
        SessionPresentation? presentation = null;
        AgentId? creationAgent = null;
        ModelRef? creationModel = null;
        IReadOnlySet<SessionId> activeSessions = new HashSet<SessionId>();
        IReadOnlyList<SessionInfo> sessionCache = [];
        var imageLoaders = new List<ImageSourceLoader>();

        async Task<PromptConfiguration> ReadReadiness(SessionInfo? session, CancellationToken cancellationToken)
        {
            var capabilities = await readiness!.ReadAsync(cancellationToken);
            var placement = session?.Location ?? homeLocation;
            if (catalog is null || catalogLocation != placement) await LoadCatalogAt(placement, cancellationToken);
            IReadOnlyList<McpServer> mcp = [];
            string? mcpError = null;
            try { mcp = (await api!.ListMcpServersAsync(placement.Directory, placement.WorkspaceId?.Value, cancellationToken)).Data; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { mcpError = SessionClientAdapter.Describe(exception); }
            IReadOnlyList<CommandInfo> commands = [];
            string? commandError = null;
            try { commands = (await api!.ListCommandsAsync(placement.Directory, placement.WorkspaceId?.Value, cancellationToken)).Data; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { commandError = SessionClientAdapter.Describe(exception); }
            SkillCatalogSnapshot? skills = null;
            string? skillError = null;
            try { skills = new(await api!.ListSkillsAsync(placement.Directory, placement.WorkspaceId?.Value, cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { skillError = SessionClientAdapter.Describe(exception); }
            presentation = new(session, placement, catalog?.Models ?? [], mcp, mcpError,
                catalog?.Agents, catalog?.Providers, commands, commandError, skills, skillError);
            ModelInfo? defaultModel = null;
            if (session?.Model is null && api is not null)
            {
                try { defaultModel = (await api.DefaultModelAsync(placement.Directory, placement.WorkspaceId?.Value, cancellationToken)).Data; }
                catch (SessionApiException) { }
                catch (SessionProtocolException) { }
            }
            var location = placement.Directory;
            var agents = catalog?.Agents ?? (api is not null ? (await api.ListAgentsAsync(location, placement.WorkspaceId?.Value, cancellationToken)).Data : []);
            // Native AgentCatalog places the configured selectable default first;
            // ResolveAsync(null) uses this same order. Do not invent a local agent.
            var agent = session?.Agent is { } selectedAgent ? agents.FirstOrDefault(item => item.Id.Value == selectedAgent)
                : agents.FirstOrDefault(item => !item.Hidden && item.Mode != AgentMode.Subagent);
            var creationFallback = defaultModel is null ? null : new ModelRef(defaultModel.ProviderId.Value, defaultModel.Id.Value);
            var selected = session?.Model ?? (session is null ? agent?.Model ?? creationFallback : null);
            var model = selected is { } selectedModel
                ? catalog?.Models.FirstOrDefault(item => item.ProviderId.Value == selectedModel.ProviderId && item.Id.Value == selectedModel.Id)
                : defaultModel;
            var providerId = selected?.ProviderId ?? model?.ProviderId.Value;
            var provider = catalog?.Providers.FirstOrDefault(item => item.Id.Value == providerId);
            var reason = !capabilities.CanExecute ? capabilities.UnavailableReason ?? "Server execution is unavailable."
                : !capabilities.Instructions ? "The server does not provide native instructions; no degraded execution mode was selected." : null;
            var uri = new Uri(endpoint!.Url);
            return new(agent?.Name ?? session?.Agent, model?.Name ?? selected?.Id, provider?.Name ?? providerId,
                selected?.Variant, ExecutionError: reason, Connection: $"Server {uri.Host}:{uri.Port}",
                SessionTitle: session?.Title, SessionId: session?.Id,
                ModelSelection: session?.Model, AgentSelection: agent?.Id,
                ChildSession: session?.ParentId is not null, AgentModel: agent?.Model, CreationFallback: creationFallback,
                Directory: location, Location: placement);
        }

        async Task<PromptConfiguration> Reload(CancellationToken cancellationToken)
        {
            try
            {
                var selected = await ServiceDaemon.EnsureWithOptionsAsync(new ServiceStartOptions { Server = explicitServer, Clock = clock }, cancellationToken);
                if (endpoint != selected || adapter is null)
                {
                    var sessionId = adapter?.SessionId;
                    if (adapter is not null) await adapter.DisposeAsync();
                    readiness?.Dispose();
                    api?.Dispose();
                    endpoint = selected;
                    readiness = new(selected);
                    api = new(selected);
                    catalog = null;
                    adapter = new(selected, ReadReadiness, () => new SessionCreateInput(Location: homeLocation,
                        Agent: creationAgent?.Value, Model: creationModel), sessionId, clock: clock);
                }
                return await adapter.PrepareAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                return new(null, null, null, null, ExecutionError: SessionClientAdapter.Describe(exception));
            }
        }

        async Task<SessionHttpClient> RequireApi(CancellationToken cancellationToken)
        {
            if (api is not null) return api;
            var configuration = await Reload(cancellationToken);
            return api ?? throw new InvalidOperationException(configuration.ExecutionError ?? "The selected server is unavailable.");
        }

        Task<AppCatalog> LoadCatalog(CancellationToken cancellationToken) =>
            LoadCatalogAt(adapter?.CurrentSession?.Location ?? homeLocation, cancellationToken);

        async Task<AppCatalog> LoadCatalogAt(LocationRef location, CancellationToken cancellationToken)
        {
            var client = await RequireApi(cancellationToken);
            var models = client.ListModelsAsync(location.Directory, location.WorkspaceId?.Value, cancellationToken);
            var providers = client.ListProvidersAsync(location.Directory, location.WorkspaceId?.Value, cancellationToken);
            var agents = client.ListAgentsAsync(location.Directory, location.WorkspaceId?.Value, cancellationToken);
            await Task.WhenAll(models, providers, agents);
            ModelInfo? fallback = null;
            try { fallback = (await client.DefaultModelAsync(location.Directory, location.WorkspaceId?.Value, cancellationToken)).Data; }
            catch (SessionProtocolException) { }
            IReadOnlyList<IntegrationInfo>? integrations = null;
            string? integrationError = null;
            try { integrations = (await client.ListIntegrationsAsync(location.Directory, location.WorkspaceId?.Value, cancellationToken)).Data; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { integrationError = "Could not load integrations: " + SessionClientAdapter.Describe(exception); }
            catalogLocation = location;
            return catalog = new((await models).Data, (await providers).Data, (await agents).Data, fallback, integrations, integrationError);
        }

        async Task<SessionPickerPage> LoadSessions(SessionPickerQuery query, CancellationToken cancellationToken)
        {
            var client = await RequireApi(cancellationToken);
            var page = await client.ListAsync(new SessionListQuery
            {
                Limit = query.Limit, Order = SessionOrder.Descending, RootOnly = true, Search = query.Search,
                Cursor = query.Cursor, Directory = query.AllProjects ? null : adapter?.CurrentSession?.Location.Directory ?? directory
            }, cancellationToken);
            // A failed active read is not an empty/idle projection. Let the picker report it
            // while retaining its previous page and the last successful active snapshot.
            activeSessions = (await client.ActiveAsync(cancellationToken)).Data.Keys.Select(SessionId.FromExisting).ToHashSet();
            sessionCache = sessionCache.Concat(page.Data).GroupBy(session => session.Id).Select(group => group.Last()).ToArray();
            return new(page.Data, page.Cursor.Next);
        }

        async IAsyncEnumerable<SessionResponseSnapshot> Prompt(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var current = adapter ?? throw new InvalidOperationException("The server is not connected. Reload configuration to reconnect.");
            await foreach (var update in current.PromptAsync(prompt, cancellationToken)) yield return update;
        }

        try
        {
            // One lazy parser/cache for both Markdown fences and tool diff hunks. Host/component
            // disposal runs first, so borrowed capture requests drain before the provider retires.
            await using var syntax = new TreeSitterHighlighter(clock);
            await using var host = new OpenTuiHost(clock: clock);
            var recoveryDirectories = new RecoveryDirectoryClient(RequireApi);
            SettingsRuntimeBindings.RegisterInteraction(settings, host.Interaction);
            await host.RunAsync<OpenCodeApp>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OpenCodeApp.NetworkPrompt)] = (Func<string, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>)Prompt,
                [nameof(OpenCodeApp.NetworkPromptInput)] = (Func<SessionId?, SessionPromptInput, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>)((origin, input, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).PromptAsync(origin, input, token)),
                [nameof(OpenCodeApp.NetworkSelectedPrompt)] = (Func<SessionId?, SessionPromptInput, PromptSelection, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>)((origin, input, selection, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).PromptAsync(origin, input, selection, token)),
                [nameof(OpenCodeApp.OpenHomeLocation)] = (Func<LocationRef, CancellationToken, Task<PromptConfiguration>>)(async (location, token) =>
                {
                    await RequireApi(token);
                    homeLocation = location;
                    adapter!.NewConversation();
                    return await adapter.PrepareAsync(token);
                }),
                [nameof(OpenCodeApp.ReadAdmissionAvailability)] = (Func<SessionId?, SessionPromptInput?, SessionAdmissionAvailability>)((origin, input) =>
                    adapter?.CanAdmit(origin, input) ?? new(false, "The server is not connected.", [])),
                [nameof(OpenCodeApp.ReloadConfiguration)] = (Func<CancellationToken, Task<PromptConfiguration>>)Reload,
                [nameof(OpenCodeApp.RequireSessionClient)] = (Func<CancellationToken, Task<SessionHttpClient>>)RequireApi,
                [nameof(OpenCodeApp.ReadSessionClient)] = (Func<SessionHttpClient?>)(() => api),
                [nameof(OpenCodeApp.ReadManagementRevision)] = (Func<LocationRef, ManagementRevision>)(location =>
                    adapter?.ReadManagementRevision(location) ?? default),
                [nameof(OpenCodeApp.ReloadOnStart)] = true,
                [nameof(OpenCodeApp.Keybindings)] = keybindings,
                [nameof(OpenCodeApp.Themes)] = themes,
                [nameof(OpenCodeApp.TranscriptCodeHighlighter)] = syntax,
                [nameof(OpenCodeApp.ApplicationThemeCatalog)] = themeCatalog,
                [nameof(OpenCodeApp.ModelPreferenceState)] = modelPreferences,
                [nameof(OpenCodeApp.ModelPreferenceLoadError)] = modelPreferencesError,
                [nameof(OpenCodeApp.LoadStatusMcp)] = (Func<LocationRef, CancellationToken, Task<LocationResponse<IReadOnlyList<McpServer>>>>)(async (location, token) =>
                    await (await RequireApi(token)).ListMcpServersAsync(location.Directory, location.WorkspaceId?.Value, token)),
                [nameof(OpenCodeApp.Clipboard)] = host.Clipboard,
                [nameof(OpenCodeApp.ReadPromptClipboard)] = (Func<ClipboardReadRequest, CancellationToken, Task<ClipboardReadResult>>)host.ReadClipboardAsync,
                [nameof(OpenCodeApp.ApplyRenderColors)] = (Action<TerminalRenderColors>)(colors => host.Colors = colors),
                [nameof(OpenCodeApp.InlineTextMarksSupported)] = true,
                [nameof(OpenCodeApp.ReadPromptMarkMetrics)] = (Func<TerminalTextMarkMetrics?>)(() => TerminalTextMarkMetrics.FromInput(host.Renderer.RootNode, "prompt")),
                [nameof(OpenCodeApp.FocusComponent)] = (Func<string, bool>)host.Renderer.Focus,
                [nameof(OpenCodeApp.ReadComposerAnchor)] = (Func<ComposerAnchor?>)(() => FindComposerAnchor(host.Renderer.RootNode)),
                [nameof(OpenCodeApp.CreateImageLoader)] = (Func<LocationRef, CancellationToken, ImageSourceLoader>)((location, lifetime) =>
                {
                    var client = api;
                    var basis = new Uri(FileReference.FileUri(location.Directory, "").TrimEnd('/') + "/");
                    var loader = new ImageSourceLoader(new ImageSourceAccess(OpenMedia: async (source, token) =>
                    {
                        var uri = Uri.TryCreate(source, UriKind.Absolute, out var absolute) ? absolute : new Uri(basis, source);
                        if (!uri.IsFile) return null; // No anonymous HTTP or local-file permission fallback.
                        if (!basis.IsBaseOf(uri)) throw new UnauthorizedAccessException("Image is outside the selected server location.");
                        if (client is null) throw new InvalidOperationException("The server image source is not connected.");
                        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
                        var file = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri;
                        var relative = Uri.UnescapeDataString(basis.MakeRelativeUri(file).OriginalString);
                        return new MemoryStream(await client.ReadFileAsync(relative, location.Directory, location.WorkspaceId?.Value, request.Token), writable: false);
                    }));
                    imageLoaders.Add(loader);
                    return loader;
                }),
                [nameof(OpenCodeApp.FindPromptFiles)] = (Func<LocationRef, FileSystemFindInput, CancellationToken, Task<LocationResponse<IReadOnlyList<FileSystemEntry>>>>)(async (location, input, token) =>
                    await (await RequireApi(token)).FindFilesAsync(input, location.Directory, location.WorkspaceId?.Value, token)),
                [nameof(OpenCodeApp.AdmitCommand)] = (Func<CommandSubmission, Func<SessionInfo, Task>, CancellationToken, Task<SessionInfo>>)(async (submission, ready, token) =>
                {
                    await RequireApi(token);
                    return await adapter!.ExecuteObservedCommandAsync(submission, ready, token);
                }),
                [nameof(OpenCodeApp.CopyFormLink)] = (Func<string, CancellationToken, Task>)host.Clipboard.WriteTextAsync,
                [nameof(OpenCodeApp.ReadFormClipboard)] = (Func<CancellationToken, Task<string?>>)(async token =>
                {
                    // Called only by the form/integration's explicit paste action on the dispatcher.
                    var result = await host.ReadClipboardAsync(new(["text/plain"]), token);
                    if (result.Status == ClipboardReadStatus.Empty) return null;
                    if (result.Status == ClipboardReadStatus.Read && result.Representation is { MimeType: "text/plain" } text) return text.ReadText();
                    throw new InvalidOperationException(result.Error?.Message ?? $"Clipboard text read did not complete: {result.Status}.");
                }),
                [nameof(OpenCodeApp.StageMessageRevert)] = (Func<MessageTarget, CancellationToken, Task<SessionRevert>>)(async (target, token) =>
                    (await (await RequireApi(token)).StageRevertAsync(target.SessionId, target.MessageId, ct: token)).Data),
                [nameof(OpenCodeApp.ForkBeforeMessage)] = (Func<MessageTarget, CancellationToken, Task<SessionInfo>>)(async (target, token) =>
                    (await (await RequireApi(token)).ForkAsync(target.SessionId, new ForkRequestBoundaryBefore(target.MessageId), token)).Data),
                [nameof(OpenCodeApp.ReconcileMessageTarget)] = (Func<MessageTarget, CancellationToken, Task>)(async (target, token) =>
                {
                    var current = adapter ?? throw new InvalidOperationException("The server is not connected.");
                    var observation = await current.RefreshObservationAsync(target.SessionId, token);
                    if (observation.Error is not null) throw new InvalidOperationException(observation.Error);
                }),
                [nameof(OpenCodeApp.CreateThemePersistence)] = (Func<ThemeState, Action<Exception>, ThemeSettingsPersistence>)((state, report) =>
                    new ThemeSettingsPersistence(settings, settingsStore, themeCatalog, state, report)),
                [nameof(OpenCodeApp.Settings)] = settings,
                [nameof(OpenCodeApp.LoadTabs)] = (Func<CancellationToken, Task<SessionTabLayout>>)tabs.LoadAsync,
                [nameof(OpenCodeApp.SaveTabs)] = (Func<SessionTabLayout, SessionTabLayout, CancellationToken, Task>)tabs.SaveAsync,
                [nameof(OpenCodeApp.ReadTabActivity)] = (Func<IReadOnlyDictionary<SessionId, SessionTabActivity>>)(() => adapter?.TabActivity
                    ?? System.Collections.Immutable.ImmutableDictionary<SessionId, SessionTabActivity>.Empty),
                [nameof(OpenCodeApp.OpenTabSession)] = (Func<SessionId, CancellationToken, Task<PromptConfiguration>>)(async (sessionId, token) =>
                {
                    await RequireApi(token);
                    return await adapter!.OpenSessionAsync(sessionId, token);
                }),
                [nameof(OpenCodeApp.LoadCatalog)] = (Func<CancellationToken, Task<AppCatalog>>)LoadCatalog,
                [nameof(OpenCodeApp.ConfigureNewSession)] = (Action<AgentId?, ModelRef?>)((agent, model) =>
                {
                    creationAgent = agent;
                    creationModel = model;
                }),
                [nameof(OpenCodeApp.LoadSessions)] = (Func<SessionPickerQuery, CancellationToken, Task<SessionPickerPage>>)LoadSessions,
                [nameof(OpenCodeApp.ReadSessionCache)] = (Func<IReadOnlyList<SessionInfo>>)(() => sessionCache),
                [nameof(OpenCodeApp.ReportSessionViewed)] = (Func<SessionId, double, CancellationToken, Task>)(async (id, idle, token) =>
                {
                    await (await RequireApi(token)).ViewAsync(id, idle, token);
                    sessionCache = sessionCache.Select(session => session.Id == id
                        ? session with { Time = session.Time with { Viewed = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)idle)) } } : session).ToArray();
                }),
                [nameof(OpenCodeApp.RenameSession)] = (Func<SessionId, string, CancellationToken, Task>)(async (id, title, token) =>
                {
                    await (await RequireApi(token)).RenameAsync(id, title, token);
                    sessionCache = sessionCache.Select(session => session.Id == id ? session with { Title = title } : session).ToArray();
                }),
                [nameof(OpenCodeApp.DeleteSession)] = (Func<SessionId, CancellationToken, Task>)(async (id, token) =>
                {
                    await (await RequireApi(token)).DeleteAsync(id, token);
                    adapter?.ForgetDeletedSession(id);
                    sessionCache = sessionCache.Where(session => session.Id != id).ToArray();
                }),
                [nameof(OpenCodeApp.ReadActiveSessions)] = (Func<IReadOnlySet<SessionId>>)(() => activeSessions),
                [nameof(OpenCodeApp.ReadPermissions)] = (Func<IReadOnlyList<PermissionRequest>>)(() => adapter?.Permissions ?? []),
                [nameof(OpenCodeApp.ReadPersistentPermissionGrants)] = (Func<bool>)(() => adapter?.PersistentPermissionGrants == true),
                [nameof(OpenCodeApp.ReadForms)] = (Func<SessionFormSnapshot?>)(() => adapter?.Forms),
                [nameof(OpenCodeApp.ReadSessionObservation)] = (Func<SessionId, SessionObservationSnapshot?>)(id => adapter?.ReadObservation(id)),
                [nameof(OpenCodeApp.ReadActivityFeed)] = (Func<ActivityFeedSnapshot?>)(() => adapter?.ActivityFeed),
                [nameof(OpenCodeApp.LoadActivityFamily)] = (Func<SessionId, CancellationToken, Task<IReadOnlyList<SessionInfo>>>)(async (id, token) =>
                {
                    await RequireApi(token);
                    return await adapter!.LoadActivityFamilyAsync(id, token);
                }),
                [nameof(OpenCodeApp.ObserveActivitySession)] = (Func<SessionId, CancellationToken, Task<SessionObservationSnapshot>>)((id, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).ObserveSessionAsync(id, token)),
                [nameof(OpenCodeApp.MergeActivityShell)] = (Action<LocatedShell, long?>)((shell, revision) => adapter?.MergeActivityShell(shell, revision)),
                [nameof(OpenCodeApp.RemoveActivityShell)] = (Action<LocationRef, ShellId, long?>)((location, id, revision) => adapter?.RemoveActivityShell(location, id, revision)),
                [nameof(OpenCodeApp.RunSessionShell)] = (Func<SessionShellSubmission, Func<SessionInfo, Task>, CancellationToken, Task>)(async (submission, ready, token) =>
                {
                    await RequireApi(token);
                    await adapter!.ExecuteObservedShellAsync(submission, ready, token);
                }),
                [nameof(OpenCodeApp.ReadRecoveryFeed)] = (Func<SessionFeedSnapshot?>)(() => adapter?.Feed),
                [nameof(OpenCodeApp.LoadRecoveryDirectories)] = (Func<ProjectId, LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>)recoveryDirectories.WorktreesAsync,
                [nameof(OpenCodeApp.BrowseRecoveryDirectory)] = (Func<LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>)recoveryDirectories.ChildrenAsync,
                [nameof(OpenCodeApp.SubmitRecoveryMove)] = (Func<SessionId, LocationRef, InboxDeliveryMode?, CancellationToken, Task<SessionMoveSnapshot>>)((id, destination, delivery, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).MoveSessionAsync(id, destination, delivery, token)),
                [nameof(OpenCodeApp.RetryRecoveryFeed)] = (Func<CancellationToken, Task>)(token =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).RetryConnectionAsync(token)),
                [nameof(OpenCodeApp.ReloadRecoverySession)] = (Func<SessionId, CancellationToken, Task<SessionObservationSnapshot>>)((id, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).ReloadSessionAsync(id, token)),
                [nameof(OpenCodeApp.MutateInbox)] = (Func<SessionId, MessageId, PendingInputAction, CancellationToken, Task>)(async (id, item, action, token) =>
                {
                    var client = await RequireApi(token);
                    if (action == PendingInputAction.Steer) await client.SteerInboxAsync(id, item, token);
                    else if (action == PendingInputAction.Queue) await client.QueueInboxAsync(id, item, token);
                    else await client.CancelInboxAsync(id, item, token);
                }),
                [nameof(OpenCodeApp.RefreshInbox)] = (Func<SessionId, CancellationToken, Task>)(async (id, token) =>
                {
                    var current = adapter ?? throw new InvalidOperationException("The server is not connected.");
                    var snapshot = await current.RefreshObservationAsync(id, token);
                    if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
                }),
                [nameof(OpenCodeApp.ReadDeletedTabSessions)] = (Func<IReadOnlySet<SessionId>>)(() => adapter?.DeletedSessions ?? new HashSet<SessionId>()),
                [nameof(OpenCodeApp.InterruptObservedSession)] = (Func<SessionId, CancellationToken, Task>)((id, token) =>
                    (adapter ?? throw new InvalidOperationException("The server is not connected.")).InterruptSessionAsync(id, token)),
                [nameof(OpenCodeApp.OpenFormLink)] = (Func<string, CancellationToken, Task>)FormExternalActions.OpenAsync,
                [nameof(OpenCodeApp.ReadPresentation)] = (Func<SessionPresentation?>)(() => presentation),
                [nameof(OpenCodeApp.RefreshForms)] = (Func<CancellationToken, Task>)(async token =>
                {
                    await RequireApi(token);
                    await adapter!.RefreshCurrentFormsAsync(token);
                }),
                [nameof(OpenCodeApp.ReplyForm)] = (Func<FormReplyRequest, CancellationToken, Task>)(async (request, token) =>
                {
                    var current = adapter ?? throw new InvalidOperationException("The server is not connected.");
                    await current.ReplyFormAsync(request, token);
                }),
                [nameof(OpenCodeApp.CancelForm)] = (Func<FormCancelRequest, CancellationToken, Task>)(async (request, token) =>
                {
                    var current = adapter ?? throw new InvalidOperationException("The server is not connected.");
                    await current.CancelFormAsync(request, token);
                }),
                [nameof(OpenCodeApp.RefreshPermissions)] = (Func<CancellationToken, Task>)(token => adapter?.RefreshCurrentPermissionsAsync(token) ?? Task.CompletedTask),
                [nameof(OpenCodeApp.ReplyPermission)] = (Func<PermissionDecision, CancellationToken, Task>)(async (decision, token) =>
                {
                    var current = adapter ?? throw new InvalidOperationException("The server is not connected.");
                    await current.ReplyPermissionAsync(decision.SessionId, decision.RequestId, decision.Reply, decision.Feedback, token);
                }),
                [nameof(OpenCodeApp.OpenSession)] = (Func<SessionInfo, CancellationToken, Task<PromptConfiguration>>)(async (session, token) =>
                {
                    await RequireApi(token);
                    return await adapter!.OpenSessionAsync(session.Id, token);
                }),
                [nameof(OpenCodeApp.CreateSession)] = (Func<CancellationToken, Task<PromptConfiguration>>)(async token =>
                {
                    await RequireApi(token);
                    homeLocation = adapter!.CurrentSession?.Location ?? homeLocation;
                    await adapter.CreateSessionAsync(token, creationModel, creationAgent);
                    return await adapter.PrepareAsync(token);
                }),
                [nameof(OpenCodeApp.CurrentDirectory)] = directory,
                [nameof(OpenCodeApp.ExecutionError)] = "Server connection has not been established.",
                [nameof(OpenCodeApp.Version)] = typeof(InteractiveTui).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                [nameof(OpenCodeApp.NewConversation)] = (Func<CancellationToken, Task>)(token =>
                {
                    token.ThrowIfCancellationRequested();
                    adapter?.NewConversation();
                    return Task.CompletedTask;
                })
            }), title: "opencode-dotnet");
        }
        finally
        {
            foreach (var loader in imageLoaders) loader.Dispose();
            try { if (adapter is not null) await adapter.DisposeAsync(); }
            finally { readiness?.Dispose(); api?.Dispose(); }
        }
    }

    private static ComposerAnchor? FindComposerAnchor(TuiNode node)
    {
        if (node.FocusKey == "command-composer" && node.LayoutWidth > 0 && node.LayoutHeight > 0)
            return new(node.X, node.Y, node.LayoutWidth, node.LayoutHeight);
        foreach (var child in node.LayoutChildren)
            if (FindComposerAnchor(child) is { } anchor) return anchor;
        return null;
    }
}
