﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem; // Roslyn
using Microsoft.VisualStudio.ProjectSystem.Utilities;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Threading.Tasks;

namespace Microsoft.VisualStudio.ProjectSystem.LanguageServices;

/// <summary>
/// Entry point for the hosting of Roslyn language services by the .NET project system.
/// One instance of this host exists per unconfigured project.
/// </summary>
/// <remarks>
/// The core responsibilities of this host are:
///
/// <list type="bullet">
///   <item>
///     To create, populate and destroy instances of <see cref="IWorkspaceProjectContext"/> during project load,
///     evaluation/build, close and changes to the set of project configurations.
///   </item>
///   <item>
///     To manage operation progress registrations so that IDE features wait for the language service to be initialized,
///     preventing things like error squigglies appearing during project load.
///   </item>
///   <item>
///     To interrogate the active workspace as part of various IDE features, including acquiring a read or write lock.
///   </item>
/// </list>
/// </remarks>
[Export(typeof(IWorkspaceWriter))]
[Export(ExportContractNames.Scopes.UnconfiguredProject, typeof(IProjectDynamicLoadComponent))]
[AppliesTo(ProjectCapability.DotNetLanguageService)]
internal sealed class LanguageServiceHost : OnceInitializedOnceDisposedUnderLockAsync, IProjectDynamicLoadComponent, IWorkspaceWriter
{
    // TODO don't activate in if _vsShellServices.Value.IsInCommandLineMode (https://github.com/dotnet/project-system/issues/3832)

    private readonly TaskCompletionSource _firstPrimaryWorkspaceSet = new();

    private readonly UnconfiguredProject _unconfiguredProject;
    private readonly IWorkspaceFactory _workspaceFactory;
    private readonly IActiveConfigurationGroupSubscriptionService _activeConfigurationGroupSubscriptionService;
    private readonly IActiveConfiguredProjectProvider _activeConfiguredProjectProvider;
    private readonly IUnconfiguredProjectTasksService _tasksService;
    private readonly ISafeProjectGuidService _projectGuidService;
    private readonly IProjectFaultHandlerService _projectFaultHandler;
    private readonly JoinableTaskCollection _joinableTaskCollection;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    private DisposableBag? _disposables;
    private Workspace? _primaryWorkspace;

    [ImportingConstructor]
    public LanguageServiceHost(
        UnconfiguredProject project,
        IWorkspaceFactory workspaceFactory,
        IActiveConfigurationGroupSubscriptionService activeConfigurationGroupSubscriptionService,
        IActiveConfiguredProjectProvider activeConfiguredProjectProvider,
        IUnconfiguredProjectTasksService tasksService,
        ISafeProjectGuidService projectGuidService,
        IProjectThreadingService threadingService,
        IProjectFaultHandlerService projectFaultHandler)
        : base(threadingService.JoinableTaskContext)
    {
        _unconfiguredProject = project;
        _workspaceFactory = workspaceFactory;
        _activeConfigurationGroupSubscriptionService = activeConfigurationGroupSubscriptionService;
        _activeConfiguredProjectProvider = activeConfiguredProjectProvider;
        _tasksService = tasksService;
        _projectGuidService = projectGuidService;
        _projectFaultHandler = projectFaultHandler;

        _joinableTaskCollection = threadingService.JoinableTaskContext.CreateCollection();
        _joinableTaskCollection.DisplayName = "LanguageServiceHostTasks";
        _joinableTaskFactory = new JoinableTaskFactory(_joinableTaskCollection);
    }

    public Task LoadAsync()
    {
        return InitializeAsync(_tasksService.UnloadCancellationToken);
    }

    public Task UnloadAsync()
    {
        return DisposeAsync();
    }

    // - IActiveConfigurationGroupSubscriptionService is a data source for ConfigurationSubscriptionSources instances
    // - ConfigurationSubscriptionSources is an immutable snapshot that maps from ProjectConfigurationSlice to IActiveConfigurationSubscriptionSource
    //
    // Over time the mapping from slice to source changes. We need to have subscriptions for each in that mapping, and create/destroy as they come and go.
    // However if the underlying 'active' configuration changes, that is transparent to us.

    protected override Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // We have one "workspace" per "slice".
        //
        // - A "workspace" models the project state that Roslyn needs for a specific configuration.
        // - A "slice" represents a set of project configurations that excludes "Configuration" and "Platform".
        //
        // Slices become important for multi-targeting projects. When multiple targets exist, we create a slice for each.
        // In future, we may add other arbitrary project dimensions, which would also be covered by this.
        // If the user changes the configuration, for example from "Debug" to "Release", we keep the same slices, though
        // the data behind them updates. This allows us to re-use the Roslyn project workspace context, which means
        // Roslyn can avoid throwing away the work it did previously and reparsing everything. It's uncommon for a config
        // switch to require large amounts of work to be re-done, so this optimisation can be quite impactful.
        //
        // Over time the set of slices may grow or contract, and we track that here.

        var workspaceBySlice = new Dictionary<ProjectConfigurationSlice, Workspace>();

        _disposables = new()
        {
            ProjectDataSources.SyncLinkTo(
                // We track the primary active configuration (i.e. first in list) via this source.
                _activeConfiguredProjectProvider.ActiveConfiguredProjectBlock.SyncLinkOptions(),
                // We track per-slice data via this source.
                _activeConfigurationGroupSubscriptionService.SourceBlock.SyncLinkOptions(),
                target: DataflowBlockFactory.CreateActionBlock<IProjectVersionedValue<(ConfiguredProject ActiveConfiguredProject, ConfigurationSubscriptionSources Sources)>>(
                    async update => await ExecuteUnderLockAsync(cancellationToken => OnSlicesChanged(update, cancellationToken)),
                    _unconfiguredProject,
                    ProjectFaultSeverity.LimitedFunctionality),
                linkOptions: DataflowOption.PropagateCompletion,
                cancellationToken: cancellationToken)
        };

        return Task.CompletedTask;

        async Task OnSlicesChanged(IProjectVersionedValue<(ConfiguredProject ActiveConfiguredProject, ConfigurationSubscriptionSources Sources)> update, CancellationToken cancellationToken)
        {
            ProjectConfiguration activeProjectConfiguration = update.Value.ActiveConfiguredProject.ProjectConfiguration;
            IReadOnlyDictionary<ProjectConfigurationSlice, IActiveConfigurationSubscriptionSource> sources = update.Value.Sources;

            // Check off existing slices. An unseen at the end must be disposed.
            var checklist = new Dictionary<ProjectConfigurationSlice, Workspace>(workspaceBySlice);

            // TODO currently this loops through each slice, initializing them serially. can we do this in parallel, or can we do the active slice first?

            foreach ((ProjectConfigurationSlice slice, IActiveConfigurationSubscriptionSource source) in sources)
            {
                if (!workspaceBySlice.TryGetValue(slice, out Workspace? workspace))
                {
                    Assumes.False(checklist.ContainsKey(slice));

                    Guid projectGuid = await _projectGuidService.GetProjectGuidAsync(cancellationToken);

                    // New slice. Create a workspace for it.
                    workspace = _workspaceFactory.Create(source, slice, _joinableTaskFactory, projectGuid, cancellationToken);

                    if (workspace is null)
                    {
                        System.Diagnostics.Debug.Fail($"Failed to construct workspace for {slice}.");
                        continue;
                    }

                    workspaceBySlice.Add(slice, workspace);
                }
                else
                {
                    // We have seen this slice, so remove it from the list we're tracking
                    Assumes.True(checklist.Remove(slice));
                }

                workspace.IsPrimary = IsPrimaryActiveSlice(slice, activeProjectConfiguration);

                if (workspace.IsPrimary)
                {
                    _primaryWorkspace = workspace;
                    _firstPrimaryWorkspaceSet.TrySetResult();
                }
            }

            // Dispose workspaces for unseen slices
            foreach ((_, Workspace workspace) in checklist)
            {
                if (ReferenceEquals(_primaryWorkspace, workspace))
                {
                    _primaryWorkspace = null;
                }

                workspace.IsPrimary = false;

                // Disposes asynchronously on the thread pool, without awaiting completion.
                workspace.Dispose();
            }
        }

        static bool IsPrimaryActiveSlice(ProjectConfigurationSlice slice, ProjectConfiguration activeProjectConfiguration)
        {
            // If all slice dimensions are present with the same value in the configuration, then this is a match.
            foreach ((string name, string value) in slice.Dimensions)
            {
                if (activeProjectConfiguration.Dimensions.TryGetValue(name, out string activeValue) &&
                    StringComparers.ConfigurationDimensionValues.Equals(value, activeValue))
                {
                    continue;
                }

                // The dimension's value is either unknown, or the value differs. This is not a match.
                return false;
            }

            // All dimensions in the slice match the project configuration.
            // If the slice's configuration is empty, we also return true.
            return true;
        }
    }

    #region IWorkspaceWriter

    public async Task WhenInitialized(CancellationToken token)
    {
        await _firstPrimaryWorkspaceSet.Task.WithCancellation(token);
    }

    public async Task WriteAsync(Func<IWorkspace, Task> action, CancellationToken token)
    {
        token = _tasksService.LinkUnload(token);

        Workspace workspace = await GetPrimaryWorkspaceAsync(token);

        await workspace.WriteAsync(action, token);
    }

    public async Task<T> WriteAsync<T>(Func<IWorkspace, Task<T>> action, CancellationToken token)
    {
        token = _tasksService.LinkUnload(token);

        Workspace workspace = await GetPrimaryWorkspaceAsync(token);

        return await workspace.WriteAsync(action, token);
    }

    private async Task<Workspace> GetPrimaryWorkspaceAsync(CancellationToken cancellationToken)
    {
        await WhenProjectLoaded(cancellationToken);

        return _primaryWorkspace ?? throw Assumes.Fail("Primary workspace unknown.");
    }

    private async Task WhenProjectLoaded(CancellationToken cancellationToken)
    {
        // The active configuration can change multiple times during initialization in cases where we've incorrectly
        // guessed the configuration via our IProjectConfigurationDimensionsProvider3 implementation.
        // Wait until that has been determined before we publish the wrong configuration.
        await _tasksService.PrioritizedProjectLoadedInHost.WithCancellation(cancellationToken);

        // We block project load on initialization of the primary workspace.
        // Therefore by this point we must have set the primary workspace.
        System.Diagnostics.Debug.Assert(_firstPrimaryWorkspaceSet.Task is { IsCompleted: true, IsFaulted: false });
    }

    #endregion

    [ProjectAutoLoad(startAfter: ProjectLoadCheckpoint.AfterLoadInitialConfiguration, completeBy: ProjectLoadCheckpoint.ProjectFactoryCompleted)]
    [AppliesTo(ProjectCapability.DotNetLanguageService)]
    public Task AfterLoadInitialConfigurationAsync()
    {
        // Ensure the project is not considered loaded until our first publication.
        Task result = _tasksService.PrioritizedProjectLoadedInHostAsync(async () =>
        {
            using (_joinableTaskCollection.Join())
            {
                await WhenInitialized(_tasksService.UnloadCancellationToken);
            }
        });

        // While we want make sure it's loaded before PrioritizedProjectLoadedInHost,
        // we don't want to block project factory completion on its load, so fire and forget.
        _projectFaultHandler.Forget(result, _unconfiguredProject, ProjectFaultSeverity.LimitedFunctionality);

        return Task.CompletedTask;
    }

    protected override Task DisposeCoreUnderLockAsync(bool initialized)
    {
        _firstPrimaryWorkspaceSet.TrySetCanceled();

        _disposables?.Dispose();

        return Task.CompletedTask;
    }
}
